using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Storage;

namespace DiskScope.UI;

/// <summary>Lists saved scans; open, delete one or all, open the data folder.</summary>
sealed class DataManagerForm : Form
{
    readonly SnapshotCatalog _catalog;
    readonly AppSettings _settings;
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
    readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, Padding = new Padding(10, 8, 10, 4) };
    readonly CheckBox _autoSave = new() { AutoSize = true };
    readonly CheckBox _loadLast = new() { AutoSize = true };
    readonly Button _open = new() { AutoSize = true };
    readonly Button _delete = new() { AutoSize = true };
    readonly Button _deleteAll = new() { AutoSize = true };
    readonly Button _folder = new() { AutoSize = true };
    readonly Button _close = new() { AutoSize = true, DialogResult = DialogResult.Cancel };

    /// <summary>The snapshot the user chose to open, if any.</summary>
    public SnapshotInfo? ToOpen { get; private set; }

    public DataManagerForm(SnapshotCatalog catalog, AppSettings settings)
    {
        _catalog = catalog;
        _settings = settings;
        Font = SystemFonts.MessageBoxFont!;
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T("DataTitle");
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(520));
        MinimizeBox = false;
        ShowInTaskbar = false;
        CancelButton = _close;

        _summary.Height = LogicalToDeviceUnits(54);
        foreach (var (key, w, align) in new[] { ("ColPath", 330, HorizontalAlignment.Left), ("ColScanned", 150, HorizontalAlignment.Left),
                     ("ColFiles", 100, HorizontalAlignment.Right), ("ColOnDisk", 100, HorizontalAlignment.Right),
                     ("ColDuration", 80, HorizontalAlignment.Right), ("ColFileSize", 100, HorizontalAlignment.Right) })
            _list.Columns.Add(L.T(key), LogicalToDeviceUnits(w), align);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };

        _autoSave.Text = L.T("AutoSave");
        _autoSave.Checked = settings.AutoSave;
        _autoSave.CheckedChanged += (_, _) => settings.AutoSave = _autoSave.Checked;
        _loadLast.Text = L.T("LoadLast");
        _loadLast.Checked = settings.LoadLastOnStart;
        _loadLast.CheckedChanged += (_, _) => settings.LoadLastOnStart = _loadLast.Checked;

        _open.Text = L.T("OpenScan");
        _open.Click += (_, _) => OpenSelected();
        _delete.Text = L.T("DeleteScans");
        _delete.Click += (_, _) => DeleteSelected();
        _deleteAll.Text = L.T("DeleteAllScans");
        _deleteAll.Click += (_, _) => DeleteAll();
        _folder.Text = L.T("OpenDataFolder");
        _folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(_catalog.Directory);
            Process.Start(new ProcessStartInfo(_catalog.Directory) { UseShellExecute = true });
        };
        _close.Text = L.T("Close");

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 0) };
        options.Controls.AddRange([_autoSave, _loadLast]);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange([_open, _delete, _deleteAll, _folder, _close]);
        Controls.Add(_list);
        Controls.Add(_summary);
        Controls.Add(options);
        Controls.Add(buttons);
        Fill();
    }

    void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _catalog.List())
        {
            var item = new ListViewItem(s.RootPath + (s.Complete ? "" : "  " + L.T("Incomplete"))) { Tag = s };
            item.SubItems.Add(s.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture));
            item.SubItems.Add(Format.Count(s.Files, L.Culture));
            item.SubItems.Add(Format.Size(s.Size, L.Culture));
            item.SubItems.Add(Format.Duration(s.Duration));
            item.SubItems.Add(Format.Size(s.FileBytes, L.Culture));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _summary.Text = L.T("DataSummary", _list.Items.Count, Format.Size(_catalog.TotalBytes(), L.Culture), _catalog.Directory);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        _open.Enabled = _list.SelectedItems.Count == 1;
        _delete.Enabled = _list.SelectedItems.Count > 0;
        _deleteAll.Enabled = _list.Items.Count > 0;
    }

    void OpenSelected()
    {
        if (_list.SelectedItems.Count != 1) return;
        ToOpen = (SnapshotInfo)_list.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
    }

    void DeleteSelected()
    {
        var selected = _list.SelectedItems.Cast<ListViewItem>().Select(i => (SnapshotInfo)i.Tag!).ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show(this, L.T("ConfirmDeleteScans", selected.Count), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var s in selected) TryDelete(() => _catalog.Delete(s));
        Fill();
    }

    void DeleteAll()
    {
        if (MessageBox.Show(this, L.T("ConfirmDeleteAllScans", _list.Items.Count, Format.Size(_catalog.TotalBytes(), L.Culture)), Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        TryDelete(_catalog.DeleteAll);
        Fill();
    }

    void TryDelete(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
