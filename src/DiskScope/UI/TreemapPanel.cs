using System.ComponentModel;
using System.Drawing.Drawing2D;
using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>File type groups for coloring the map.</summary>
enum FileKind
{
    Video,
    Image,
    Audio,
    Archive,
    DiskImage,
    Program,
    Document,
    Code,
    Other,
}

/// <summary>A treemap of one folder: area is on-disk size, color is the file type. Click selects, double-click opens a folder.</summary>
sealed class TreemapView : Control
{
    static readonly Dictionary<string, FileKind> Kinds = BuildKinds();

    public static readonly Dictionary<FileKind, Color> Colors = new()
    {
        [FileKind.Video] = Color.FromArgb(155, 89, 182),
        [FileKind.Image] = Color.FromArgb(46, 160, 67),
        [FileKind.Audio] = Color.FromArgb(22, 160, 160),
        [FileKind.Archive] = Color.FromArgb(230, 126, 34),
        [FileKind.DiskImage] = Color.FromArgb(141, 98, 64),
        [FileKind.Program] = Color.FromArgb(214, 69, 65),
        [FileKind.Document] = Color.FromArgb(52, 120, 200),
        [FileKind.Code] = Color.FromArgb(100, 116, 139),
        [FileKind.Other] = Color.FromArgb(160, 160, 160),
    };

    readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100 };
    NodeStore? _store;
    int _root = NodeStore.None;
    List<TreemapItem> _items = [];
    Bitmap? _picture;
    int _hover = -1;

    public event Action<int>? NodeClicked;
    public event Action<int>? FolderOpened;

    public TreemapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Root => _root;

    public void Show(NodeStore? store, int root)
    {
        _store = store;
        _root = root;
        Relayout();
    }

    public void Relayout()
    {
        _picture?.Dispose();
        _picture = null;
        _items = _store != null && _root != NodeStore.None && _store.IsLive(_root) && Width > 4 && Height > 4
            ? TreemapLayout.Build(_store, _root, new RectF(0, 0, Width, Height), minSide: LogicalToDeviceUnits(3), padding: LogicalToDeviceUnits(2))
            : [];
        _hover = -1;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    public static FileKind KindOf(string name) =>
        Kinds.TryGetValue(Path.GetExtension(name), out var kind) ? kind : FileKind.Other;

    static Dictionary<string, FileKind> BuildKinds()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string exts)
        {
            foreach (var e in exts.Split(' ')) map["." + e] = kind;
        }
        Add(FileKind.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts vob 3gp");
        Add(FileKind.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico");
        Add(FileKind.Audio, "mp3 wav flac aac ogg m4a wma opus aiff mid");
        Add(FileKind.Archive, "zip rar 7z gz tgz bz2 xz zst tar cab lz4 pak");
        Add(FileKind.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 wim esd dmg bin");
        Add(FileKind.Program, "exe dll sys msi msix appx ocx drv so lib pyd node jar");
        Add(FileKind.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md epub csv");
        Add(FileKind.Code, "cs js ts py java c cpp h hpp go rs php rb json xml yml yaml html css sql pyc class");
        return map;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_picture == null && _items.Count > 0) _picture = Render();
        if (_picture != null) e.Graphics.DrawImageUnscaled(_picture, 0, 0);
        else e.Graphics.Clear(BackColor);
        if (_hover >= 0 && _hover < _items.Count)
        {
            var r = _items[_hover].Bounds;
            using var pen = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(pen, r.X + 1, r.Y + 1, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));
            using var dark = new Pen(Color.Black, 1);
            e.Graphics.DrawRectangle(dark, r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
        }
    }

    Bitmap Render()
    {
        var bmp = new Bitmap(Width, Height);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(60, 60, 60));
        var store = _store!;
        foreach (var item in _items)
        {
            var r = item.Bounds;
            var rect = new RectangleF(r.X, r.Y, r.Width, r.Height);
            if (item.IsDirectory)
            {
                // Folders: dark, a little lighter with depth, so nesting stays readable and files stand out.
                int v = Math.Min(110, 40 + item.Depth * 12);
                using var brush = new SolidBrush(Color.FromArgb(v, v, v + 8));
                g.FillRectangle(brush, rect);
                continue;
            }
            var color = Colors[KindOf(store.GetName(item.Node))];
            // A soft cushion: lighter top-left, darker bottom-right.
            using (var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)),
                       ControlPaint.Light(color, 0.6f), ControlPaint.Dark(color, 0.15f), LinearGradientMode.ForwardDiagonal))
                g.FillRectangle(brush, rect);
            using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0)))
                g.DrawRectangle(pen, r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));
            if (r.Width > 70 && r.Height > 18)
                TextRenderer.DrawText(g, store.GetName(item.Node), Font, Rectangle.Round(RectangleF.Inflate(rect, -3, -3)), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        return bmp;
    }

    int ItemAt(Point p)
    {
        // Later items are drawn on top of (inside) earlier folders, so search from the end.
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Bounds.Contains(p.X, p.Y)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = ItemAt(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Invalidate();
        if (hit >= 0 && _store != null)
        {
            int node = _items[hit].Node;
            _tip.SetToolTip(this, $"{_store.GetPath(node)}\n{Format.Size(_store[node].Size, L.Culture)}");
        }
        else _tip.SetToolTip(this, null);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int hit = ItemAt(e.Location);
        if (hit >= 0) NodeClicked?.Invoke(_items[hit].Node);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int hit = ItemAt(e.Location);
        if (hit < 0 || _store == null) return;
        int node = _items[hit].Node;
        // Double-clicking a file opens the folder it is in, one level below the shown one.
        while (node != NodeStore.None && _store[node].Parent != _root && _store[node].Parent != NodeStore.None) node = _store[node].Parent;
        if (_store[node].IsDirectory) FolderOpened?.Invoke(node);
    }
}

/// <summary>The map with a title line (the shown folder, with an Up button) and a color legend.</summary>
sealed class TreemapPanel : UserControl
{
    readonly TreemapView _view = new() { Dock = DockStyle.Fill };
    readonly Label _title = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 2) };
    readonly Button _up = new() { AutoSize = true };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 2) };
    readonly FlowLayoutPanel _legend = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
    NodeStore? _store;

    public event Action<int>? NodeClicked;
    public event Action? Shown;

    public TreemapPanel()
    {
        _bar.Controls.AddRange([_up, _title]);
        Controls.Add(_view);
        Controls.Add(_legend);
        Controls.Add(_bar);
        _view.NodeClicked += n => NodeClicked?.Invoke(n);
        _view.FolderOpened += n => ShowFolder(n);
        _up.Click += (_, _) =>
        {
            if (_store != null && _view.Root != NodeStore.None && _store[_view.Root].Parent is int p && p != NodeStore.None) ShowFolder(p);
        };
        ApplyTexts();
    }

    public void ApplyTexts()
    {
        _up.Text = "↑ " + L.T("Up");
        _legend.Controls.Clear();
        foreach (var (kind, color) in TreemapView.Colors)
        {
            var swatch = new Label { BackColor = color, Size = new Size(LogicalToDeviceUnits(14), LogicalToDeviceUnits(14)), Margin = new Padding(8, 4, 3, 2) };
            var text = new Label { Text = L.T("Kind" + kind), AutoSize = true, Margin = new Padding(0, 3, 6, 2) };
            _legend.Controls.AddRange([swatch, text]);
        }
        UpdateTitle();
    }

    public void SetStore(NodeStore? store)
    {
        _store = store;
        _view.Show(store, NodeStore.None);
        UpdateTitle();
    }

    /// <summary>Shows a folder (or the folder of a file).</summary>
    public void ShowFolder(int node)
    {
        if (_store == null || node == NodeStore.None) return;
        if (!_store[node].IsDirectory) node = _store[node].Parent;
        _view.Show(_store, node);
        UpdateTitle();
        Shown?.Invoke();
    }

    public int Folder => _view.Root;

    public void Relayout() => _view.Relayout();

    void UpdateTitle()
    {
        int root = _view.Root;
        _up.Enabled = _store != null && root != NodeStore.None && _store[root].Parent != NodeStore.None;
        _title.Text = _store == null || root == NodeStore.None ? "" :
            L.T("MapTitle", _store.GetPath(root), Format.Size(_store[root].Size, L.Culture));
    }
}
