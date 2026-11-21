using System.ComponentModel;
using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>
/// What kinds of files fill the selected folder: totals per extension grouped by kind on the left, and the largest
/// files of the selected extension on the right.
/// </summary>
sealed class FileTypesPanel : UserControl
{
    const int FilesShown = 500;

    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 4) };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 2, 4, 0) };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    readonly ListView _types = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        BorderStyle = BorderStyle.None, ShowGroups = true,
    };
    readonly ListView _files = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        BorderStyle = BorderStyle.None,
    };

    readonly ImageList _swatches = new() { ColorDepth = ColorDepth.Depth32Bit };
    NodeStore? _store;
    int _folder = NodeStore.None;
    List<FileTypeEntry> _totals = [];
    CancellationTokenSource? _cancel;
    CancellationTokenSource? _filesCancel;

    public event Action<int>? RevealRequested;

    /// <summary>Raised when the totals have been filled; used by the test hooks.</summary>
    public event Action? Filled;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? ResultMenu
    {
        get => _files.ContextMenuStrip;
        set => _files.ContextMenuStrip = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Folder => _folder;

    public FileTypesPanel()
    {
        _bar.Controls.Add(_summary);
        _split.Panel1.Controls.Add(_types);
        _split.Panel2.Controls.Add(_files);
        Controls.Add(_split);
        Controls.Add(_bar);
        foreach (var w in new[] { 150, 95, 90, 70 }) _types.Columns.Add("", w);
        foreach (var w in new[] { 280, 95, 140, 520 }) _files.Columns.Add("", w);
        for (int i = 1; i < 4; i++) _types.Columns[i].TextAlign = HorizontalAlignment.Right;
        _files.Columns[1].TextAlign = HorizontalAlignment.Right;
        _types.SelectedIndexChanged += (_, _) => ShowFiles();
        _files.DoubleClick += (_, _) =>
        {
            if (FocusedNode is int n && n != NodeStore.None) RevealRequested?.Invoke(n);
        };
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _types.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        foreach (ColumnHeader c in _files.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        _split.SplitterDistance = LogicalToDeviceUnits(440);
        // A colored square per kind, the same colors as the map.
        int size = LogicalToDeviceUnits(14);
        _swatches.ImageSize = new Size(size, size);
        _swatches.Images.Clear();
        foreach (FileKind kind in Enum.GetValues<FileKind>())
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var br = new SolidBrush(TreemapView.Colors[kind]))
                g.FillRectangle(br, 2, 2, size - 4, size - 4);
            _swatches.Images.Add(bmp);
        }
        _types.SmallImageList = _swatches;
    }

    public void ApplyTexts()
    {
        string[] typeCols = ["TypesColExt", "ColOnDisk", "ColFiles", "TypesColShare"];
        for (int i = 0; i < typeCols.Length; i++) _types.Columns[i].Text = L.T(typeCols[i]);
        string[] fileCols = ["ColName", "ColOnDisk", "ColModified", "ColFolder"];
        for (int i = 0; i < fileCols.Length; i++) _files.Columns[i].Text = L.T(fileCols[i]);
        if (_totals.Count > 0) Fill();
    }

    public void SetStore(NodeStore? store)
    {
        _cancel?.Cancel();
        _filesCancel?.Cancel();
        _store = store;
        _folder = NodeStore.None;
        _totals = [];
        _types.Items.Clear();
        _types.Groups.Clear();
        _files.Items.Clear();
        _summary.Text = "";
    }

    public int FocusedNode => _files.FocusedItem?.Tag is int n ? n : SelectedNodes().FirstOrDefault(NodeStore.None);

    public List<int> SelectedNodes() => _files.SelectedItems.Cast<ListViewItem>().Select(i => (int)i.Tag!).ToList();

    /// <summary>Counts the file types of a folder (or of a file's folder) in the background.</summary>
    public async void ShowFolder(int node)
    {
        if (_store == null || node == NodeStore.None || !_store.IsLive(node)) return;
        if (!_store[node].IsDirectory) node = _store[node].Parent;
        var store = _store;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _folder = node;
        _summary.Text = L.T("Searching");
        List<FileTypeEntry> totals;
        try
        {
            totals = await Task.Run(() => FileTypes.Totals(store, node, cancel.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (IsDisposed || cancel.IsCancellationRequested || store != _store) return;
        _cancel = null;
        string? selected = (_types.SelectedItems.Count > 0 ? _types.SelectedItems[0].Tag as FileTypeEntry : null)?.Extension;
        _totals = totals;
        Fill(selected);
        Filled?.Invoke();
    }

    /// <summary>Counts again after the tree changed (deletions, a rescan).</summary>
    public void Refresh(bool keepFolder = true)
    {
        if (_store != null && _folder != NodeStore.None && _store.IsLive(_folder) && keepFolder) ShowFolder(_folder);
    }

    void Fill(string? select = null)
    {
        var store = _store!;
        long total = _totals.Sum(t => t.Size);
        _types.BeginUpdate();
        _types.Items.Clear();
        _types.Groups.Clear();
        ListViewItem? toSelect = null;
        foreach (var kind in _totals.GroupBy(t => t.Kind).OrderByDescending(g => g.Sum(t => t.Size)))
        {
            long kindSize = kind.Sum(t => t.Size);
            var group = new ListViewGroup($"{L.T("Kind" + kind.Key)} – {Format.Size(kindSize, L.Culture)} ({Share(kindSize, total)})");
            _types.Groups.Add(group);
            foreach (var t in kind)
            {
                var item = new ListViewItem([
                    t.Extension.Length == 0 ? L.T("TypesNoExt") : t.Extension,
                    Format.Size(t.Size, L.Culture),
                    Format.Count(t.Count, L.Culture),
                    Share(t.Size, total),
                ], group) { Tag = t, ImageIndex = (int)t.Kind };
                _types.Items.Add(item);
                if (t.Extension == select) toSelect = item;
            }
        }
        _types.EndUpdate();
        _summary.Text = L.T("TypesSummary", store.GetPath(_folder), Format.Size(total, L.Culture),
            Format.Count(_totals.Sum(t => t.Count), L.Culture), Format.Count(_totals.Count, L.Culture));
        if (toSelect != null)
        {
            toSelect.Selected = true;
            toSelect.Focused = true;
            toSelect.EnsureVisible();
        }
        else _files.Items.Clear();
    }

    internal void SelectFirstForTest()
    {
        if (_types.Items.Count == 0) return;
        _types.Items[0].Selected = true;
        _types.Items[0].Focused = true;
        _types.Focus();
    }

    static string Share(long part, long total) => total > 0 ? (part * 100.0 / total).ToString("0.0", L.Culture) + " %" : "";

    async void ShowFiles()
    {
        if (_store == null || _types.SelectedItems.Count == 0 || _types.SelectedItems[0].Tag is not FileTypeEntry entry) return;
        var store = _store;
        int folder = _folder;
        _filesCancel?.Cancel();
        var cancel = _filesCancel = new CancellationTokenSource();
        List<int> files;
        try
        {
            files = await Task.Run(() => FileTypes.LargestFiles(store, folder, entry.Extension, FilesShown, cancel.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (IsDisposed || cancel.IsCancellationRequested || store != _store) return;
        _files.BeginUpdate();
        _files.Items.Clear();
        foreach (int n in files)
        {
            int parent = store[n].Parent;
            _files.Items.Add(new ListViewItem([
                store.GetName(n), Format.Size(store[n].Size, L.Culture), TreeListView.Date(store[n].MTime),
                parent == NodeStore.None ? "" : store.GetPath(parent),
            ]) { Tag = n });
        }
        _files.EndUpdate();
    }
}
