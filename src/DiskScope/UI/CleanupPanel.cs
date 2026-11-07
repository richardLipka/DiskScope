using System.ComponentModel;
using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Cleanup;

namespace DiskScope.UI;

/// <summary>Known cleanup locations found in the tree, grouped by how safe they are to clean, with advice for each.</summary>
sealed class CleanupPanel : UserControl
{
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 2) };
    readonly Button _refresh = new() { AutoSize = true };
    readonly Button _edit = new() { AutoSize = true };
    readonly Button _reload = new() { AutoSize = true };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        BorderStyle = BorderStyle.None, ShowGroups = true,
    };
    readonly TextBox _advice = new()
    {
        Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };

    NodeStore? _store;
    List<CleanupMatch> _matches = [];
    CleanupCatalog _catalog;
    string? _catalogError;

    /// <summary>The user's catalog file (copied from the built-in one on first edit).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string UserCatalogPath { get; set; } = "";

    public event Action<int>? RevealRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? ResultMenu
    {
        get => _list.ContextMenuStrip;
        set => _list.ContextMenuStrip = value;
    }

    public CleanupPanel()
    {
        _catalog = CleanupCatalog.Default();
        _bar.Controls.AddRange([_summary, _refresh, _edit, _reload]);
        Controls.Add(_list);
        Controls.Add(_advice);
        Controls.Add(_bar);
        foreach (var w in new[] { 330, 95, 120, 620 }) _list.Columns.Add("", w);
        _list.Columns[1].TextAlign = HorizontalAlignment.Right;
        _list.SelectedIndexChanged += (_, _) => ShowAdvice();
        _list.DoubleClick += (_, _) =>
        {
            if (FocusedMatch is CleanupMatch m) RevealRequested?.Invoke(m.Node);
        };
        _refresh.Click += (_, _) => FindLocations();
        _reload.Click += (_, _) =>
        {
            LoadCatalog();
            FindLocations();
        };
        _edit.Click += (_, _) => EditCatalog();
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        _advice.Height = LogicalToDeviceUnits(70);
    }

    public void ApplyTexts()
    {
        _refresh.Text = L.T("CleanupRefresh");
        _edit.Text = L.T("CleanupEdit");
        _reload.Text = L.T("CleanupReload");
        string[] cols = ["CleanupColItem", "ColOnDisk", "CleanupColAction", "ColLocation"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        if (_store != null) FindLocations();
    }

    public CleanupCatalog Catalog => _catalog;

    public void LoadCatalog()
    {
        _catalog = CleanupCatalog.Load(UserCatalogPath, out _catalogError);
    }

    public void SetStore(NodeStore? store)
    {
        _store = store;
        _matches = [];
        _list.Items.Clear();
        _summary.Text = "";
        _advice.Text = "";
    }

    public CleanupMatch? FocusedMatch => _list.FocusedItem?.Tag as CleanupMatch ?? SelectedMatches().FirstOrDefault();

    public List<CleanupMatch> SelectedMatches() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (CleanupMatch)i.Tag!).ToList();

    public async void FindLocations()
    {
        if (_store == null || _store.Root == NodeStore.None) return;
        var store = _store;
        var catalog = _catalog;
        _summary.Text = L.T("Searching");
        var matches = await Task.Run(() => CleanupFinder.Find(store, catalog));
        if (store != _store) return;
        _matches = matches;

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (CleanupSafety safety in Enum.GetValues<CleanupSafety>())
        {
            var inGroup = _matches.Where(m => m.Entry.Safety == safety).OrderByDescending(m => m.Size).ToList();
            if (inGroup.Count == 0) continue;
            var group = new ListViewGroup(L.T("Safety" + safety, Format.Size(CleanupFinder.TotalSize(store, inGroup), L.Culture)));
            _list.Groups.Add(group);
            foreach (var m in inGroup)
            {
                var item = new ListViewItem([
                    CatalogEntry.Text(m.Entry.Name, L.Lang),
                    Format.Size(m.Size, L.Culture),
                    L.T("Action" + m.Entry.Action),
                    store.GetPath(m.Node),
                ], group) { Tag = m };
                if (safety == CleanupSafety.System) item.ForeColor = SystemColors.GrayText;
                _list.Items.Add(item);
            }
        }
        _list.EndUpdate();

        long safe = CleanupFinder.TotalSize(store, _matches.Where(m => m.Entry.Safety == CleanupSafety.Safe));
        _summary.Text = L.T("CleanupSummary", _matches.Count, Format.Size(safe, L.Culture), _catalog.Entries.Count);
        _advice.Text = _catalogError != null ? L.T("CatalogError", _catalogError) : L.T("CleanupHint");
        CleanupFound?.Invoke();
    }

    /// <summary>Raised when the list has been filled; used by the test hooks.</summary>
    public event Action? CleanupFound;

    /// <summary>Drops matches whose items were deleted and updates sizes.</summary>
    public void RemoveDead()
    {
        if (_store != null && _matches.Count > 0) FindLocations();
    }

    void ShowAdvice()
    {
        if (FocusedMatch is not CleanupMatch m) return;
        var e = m.Entry;
        _advice.Text = CatalogEntry.Text(e.Name, L.Lang) + "  —  " + L.T("Safety" + e.Safety + "Short") + Environment.NewLine +
                       CatalogEntry.Text(e.Description, L.Lang) + Environment.NewLine +
                       L.T("HowToFree") + " " + CatalogEntry.Text(e.HowTo, L.Lang);
    }

    void EditCatalog()
    {
        try
        {
            if (!File.Exists(UserCatalogPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserCatalogPath)!);
                File.WriteAllText(UserCatalogPath, CleanupCatalog.DefaultJson());
            }
            // Opened with the user's editor for .json files, falling back to Notepad.
            try { Process.Start(new ProcessStartInfo(UserCatalogPath) { UseShellExecute = true }); }
            catch (Win32Exception) { Process.Start("notepad.exe", $"\"{UserCatalogPath}\""); }
            _advice.Text = L.T("CatalogEditHint", UserCatalogPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
