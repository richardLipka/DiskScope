using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiskScope.Core;

namespace DiskScope.UI;

enum Col
{
    Name,
    OnDisk,
    Percent,
    Size,
    Files,
    Folders,
    Modified,
    Accessed,
    Delta,
}

/// <summary>
/// A tree drawn into a virtual-mode list view: only the rows on screen are ever materialized,
/// so a directory with hundreds of thousands of entries scrolls as fast as one with ten.
/// </summary>
sealed class TreeListView : ListView
{
    static readonly Color BarColor = Color.FromArgb(132, 178, 235);
    static readonly Color BarTrack = Color.FromArgb(232, 238, 246);
    static readonly Color GrowColor = Color.FromArgb(192, 57, 43);
    static readonly Color ShrinkColor = Color.FromArgb(39, 136, 76);

    readonly ImageList _rowHeight = new();
    TreeRows? _rows;

    /// <summary>Enter on a row, or double-click on a file.</summary>
    public event Action<int>? NodeActivated;

    /// <summary>Backspace.</summary>
    public event Action? NavigateUp;

    /// <summary>Delete key; the argument is true with Shift (delete permanently).</summary>
    public event Action<bool>? DeleteRequested;

    /// <summary>Change against an older scan for the Delta column, or null when the node has no counterpart.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, long?>? DeltaOf { get; set; }

    public TreeListView()
    {
        View = View.Details;
        VirtualMode = true;
        OwnerDraw = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = true;
        DoubleBuffered = true;
        AllowColumnReorder = false;
        SmallImageList = _rowHeight;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TreeRows? Rows
    {
        get => _rows;
        set
        {
            _rows = value;
            SelectedIndices.Clear();
            VirtualListSize = 0;
            Reload();
            if (_rows != null && _rows.Count > 0) SelectRow(0);
        }
    }

    NodeStore? Store => _rows?.Store;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowTheme(Handle, "Explorer", null);
        UpdateRowHeight();
        UpdateSortIcon();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateRowHeight();
    }

    void UpdateRowHeight() => _rowHeight.ImageSize = new Size(1, Scale(22));

    int Scale(int px) => px * DeviceDpi / 96;

    // --- data ---

    public int NodeAtRow(int row) => _rows != null && row >= 0 && row < _rows.Count ? _rows.NodeAt(row) : NodeStore.None;

    public int FocusedNode => FocusedItem != null ? NodeAtRow(FocusedItem.Index) : SelectedNodes().FirstOrDefault(NodeStore.None);

    public List<int> SelectedNodes()
    {
        var list = new List<int>();
        if (_rows == null) return list;
        foreach (int i in SelectedIndices)
            if (i < _rows.Count) list.Add(_rows.NodeAt(i));
        return list;
    }

    /// <summary>Rebuilds the rows keeping the selection and focus on the same nodes.</summary>
    public void Reload()
    {
        if (_rows == null)
        {
            VirtualListSize = 0;
            return;
        }
        var selected = SelectedNodes();
        int focused = FocusedItem != null ? NodeAtRow(FocusedItem.Index) : NodeStore.None;
        if (!_rows.Rebuild())
        {
            Invalidate();
            return;
        }
        BeginUpdate();
        try
        {
            SelectedIndices.Clear();
            VirtualListSize = _rows.Count;
            foreach (int node in selected)
            {
                int i = _rows.IndexOf(node);
                if (i >= 0) SelectedIndices.Add(i);
            }
            int f = focused == NodeStore.None ? -1 : _rows.IndexOf(focused);
            if (f >= 0) FocusedItem = Items[f];
        }
        finally
        {
            EndUpdate();
        }
        Invalidate();
    }

    public void SelectRow(int row)
    {
        if (_rows == null || row < 0 || row >= _rows.Count) return;
        // Focus first: selection events read the focused row.
        FocusedItem = Items[row];
        SelectedIndices.Clear();
        SelectedIndices.Add(row);
        EnsureVisible(row);
    }

    public void SelectNode(int node)
    {
        if (_rows == null) return;
        int row = _rows.IndexOf(node);
        if (row < 0)
        {
            _rows.Reveal(node);
            Reload();
            row = _rows.IndexOf(node);
        }
        SelectRow(row);
    }

    public void Toggle(int node)
    {
        if (_rows == null || !_rows.HasChildren(node)) return;
        _rows.SetExpanded(node, !_rows.IsExpanded(node));
        Reload();
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        int node = NodeAtRow(e.ItemIndex);
        var item = new ListViewItem(node == NodeStore.None ? "" : Store!.GetName(node));
        for (int c = 1; c < Columns.Count; c++) item.SubItems.Add(node == NodeStore.None ? "" : CellText(node, (Col)c));
        e.Item = item;
    }

    string CellText(int node, Col col)
    {
        var s = Store!;
        ref var n = ref s[node];
        return col switch
        {
            Col.OnDisk => Format.Size(n.Size, L.Culture),
            Col.Percent => Percent(node) is double p ? p.ToString("0.0", L.Culture) + " %" : "",
            Col.Size => Format.Size(n.Length, L.Culture),
            Col.Files => n.IsDirectory ? Format.Count(n.FileCount, L.Culture) : "",
            Col.Folders => n.IsDirectory ? Format.Count(n.DirCount, L.Culture) : "",
            Col.Modified => Date(n.MTime),
            Col.Accessed => Date(n.ATime),
            Col.Delta => DeltaOf?.Invoke(node) is long d ? (d == 0 ? "0" : Format.SignedSize(d, L.Culture)) : "",
            _ => "",
        };
    }

    public static string Date(long filetime) =>
        Format.FileTime(filetime)?.ToLocalTime().ToString("g", L.Culture) ?? "";

    double? Percent(int node)
    {
        var s = Store!;
        int parent = s[node].Parent;
        if (parent == NodeStore.None) return null;
        long total = s[parent].Size;
        return total > 0 ? s[node].Size * 100.0 / total : 0;
    }

    // --- drawing ---

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => e.DrawDefault = false;

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        int node = NodeAtRow(e.ItemIndex);
        if (node == NodeStore.None) return;
        var g = e.Graphics;
        var b = e.Bounds;
        if (e.ColumnIndex == 0) b.Width = Columns[0].Width;

        bool selected = SelectedIndices.Contains(e.ItemIndex);
        Color back = selected ? (Focused ? SystemColors.Highlight : Color.FromArgb(217, 217, 217)) : BackColor;
        Color fore = selected && Focused ? SystemColors.HighlightText : ForeColor;
        using (var br = new SolidBrush(back)) g.FillRectangle(br, b);

        var col = (Col)e.ColumnIndex;
        if (col == Col.Name) DrawName(g, b, node, e.ItemIndex, fore, selected);
        else if (col == Col.Percent) DrawPercent(g, b, node, fore, selected);
        else
        {
            string text = CellText(node, col);
            if (col == Col.Delta && DeltaOf?.Invoke(node) is long d && !(selected && Focused))
                fore = d > 0 ? GrowColor : d < 0 ? ShrinkColor : SystemColors.GrayText;
            var flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (col is Col.Modified or Col.Accessed) flags = (flags & ~TextFormatFlags.Right) | TextFormatFlags.Left;
            TextRenderer.DrawText(g, text, Font, Rectangle.Inflate(b, -Scale(4), 0), fore, flags);
        }
    }

    Rectangle GlyphRect(Rectangle cell, int depth)
    {
        int size = Scale(12);
        return new Rectangle(cell.X + Scale(4) + depth * Scale(16), cell.Y + (cell.Height - size) / 2, size, size);
    }

    void DrawName(Graphics g, Rectangle b, int node, int row, Color fore, bool selected)
    {
        var s = Store!;
        ref var n = ref s[node];
        var glyph = GlyphRect(b, _rows!.DepthAt(row));
        if (_rows.HasChildren(node))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = glyph.X + glyph.Width / 2f, cy = glyph.Y + glyph.Height / 2f, r = glyph.Width / 3.2f;
            PointF[] tri = _rows.IsExpanded(node)
                ? [new(cx - r, cy - r / 2), new(cx + r, cy - r / 2), new(cx, cy + r * 0.8f)]
                : [new(cx - r / 2, cy - r), new(cx + r * 0.8f, cy), new(cx - r / 2, cy + r)];
            using var br = new SolidBrush(selected && Focused ? fore : Color.FromArgb(110, 110, 110));
            g.FillPolygon(br, tri);
            g.SmoothingMode = SmoothingMode.None;
        }

        int x = glyph.Right + Scale(4);
        int iconSize = Scale(16);
        var icon = n.Parent == NodeStore.None && s.RootPath.Length == 3 ? ShellIcons.Drive(s.RootPath)
            : n.IsDirectory ? ShellIcons.Folder : ShellIcons.ForFile(s.GetName(node));
        if (icon != null) g.DrawIcon(icon, new Rectangle(x, b.Y + (b.Height - iconSize) / 2, iconSize, iconSize));
        x += iconSize + Scale(5);

        string name = s.GetName(node);
        var flags = n.Flags;
        if ((flags & NodeFlags.Queued) != 0) name += "  " + L.T("QueuedLabel");
        else if ((flags & NodeFlags.Error) != 0) name += "  " + L.T("AccessDenied");
        else if ((flags & NodeFlags.Reparse) != 0) name += "  " + L.T("Link");
        else if ((flags & NodeFlags.HardLink) != 0) name += "  " + L.T("HardLink");
        else if (n.IsDirectory && (flags & NodeFlags.Scanned) == 0) name += "  " + L.T("NotScanned");

        Color c = fore;
        if (!(selected && Focused))
        {
            if ((flags & NodeFlags.Error) != 0) c = GrowColor;
            else if ((flags & (NodeFlags.Reparse | NodeFlags.HardLink | NodeFlags.Queued)) != 0 || (n.IsDirectory && (flags & NodeFlags.Scanned) == 0))
                c = SystemColors.GrayText;
        }
        var rect = new Rectangle(x, b.Y, Math.Max(0, b.Right - x - Scale(2)), b.Height);
        TextRenderer.DrawText(g, name, Font, rect, c,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void DrawPercent(Graphics g, Rectangle b, int node, Color fore, bool selected)
    {
        if (Percent(node) is not double p) return;
        var track = Rectangle.Inflate(b, -Scale(4), -Scale(4));
        using (var br = new SolidBrush(BarTrack)) g.FillRectangle(br, track);
        var bar = track with { Width = (int)Math.Round(track.Width * Math.Clamp(p, 0, 100) / 100) };
        using (var br = new SolidBrush(BarColor)) g.FillRectangle(br, bar);
        TextRenderer.DrawText(g, p.ToString("0.0", L.Culture) + " %", Font, track, Color.FromArgb(30, 30, 30),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    // --- input ---

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _rows != null)
        {
            var hit = HitTest(e.Location);
            if (hit.Item != null)
            {
                int row = hit.Item.Index;
                var itemRect = GetItemRect(row, ItemBoundsPortion.Entire);
                var glyph = GlyphRect(itemRect with { Width = Columns[0].Width }, _rows.DepthAt(row));
                glyph.Inflate(Scale(3), Scale(4));
                if (glyph.Contains(e.Location))
                {
                    // Rows above the toggled one do not move, so the index still points at it.
                    Toggle(_rows.NodeAt(row));
                    SelectRow(row);
                    Focus();
                    return;
                }
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var hit = HitTest(e.Location);
        if (hit.Item == null || _rows == null) return;
        int node = NodeAtRow(hit.Item.Index);
        if (Store![node].IsDirectory) Toggle(node);
        else NodeActivated?.Invoke(node);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int node = FocusedNode;
        if (_rows == null || node == NodeStore.None)
        {
            base.OnKeyDown(e);
            return;
        }
        int row = _rows.IndexOf(node);
        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.Right:
            case Keys.Add:
                if (_rows.HasChildren(node) && !_rows.IsExpanded(node)) Toggle(node);
                else if (e.KeyCode == Keys.Right && _rows.IsExpanded(node)) SelectRow(row + 1);
                break;
            case Keys.Left:
            case Keys.Subtract:
                if (_rows.IsExpanded(node) && _rows.HasChildren(node)) Toggle(node);
                else if (e.KeyCode == Keys.Left && Store![node].Parent is int p && p != NodeStore.None && _rows.IndexOf(p) is int pr && pr >= 0)
                    SelectRow(pr);
                break;
            case Keys.Enter:
                NodeActivated?.Invoke(node);
                break;
            case Keys.Back:
                NavigateUp?.Invoke();
                break;
            case Keys.Delete:
                DeleteRequested?.Invoke(e.Shift);
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else base.OnKeyDown(e);
    }

    protected override void OnColumnClick(ColumnClickEventArgs e)
    {
        base.OnColumnClick(e);
        if (_rows == null) return;
        SortColumn? sort = (Col)e.Column switch
        {
            Col.Name => SortColumn.Name,
            Col.OnDisk or Col.Percent => SortColumn.Size,
            Col.Size => SortColumn.Length,
            Col.Files => SortColumn.Files,
            Col.Modified => SortColumn.Modified,
            Col.Accessed => SortColumn.Accessed,
            Col.Delta => SortColumn.Delta,
            _ => null,
        };
        if (sort is not SortColumn s) return;
        if (_rows.Sort == s) _rows.Descending = !_rows.Descending;
        else
        {
            _rows.Sort = s;
            _rows.Descending = s != SortColumn.Name;
        }
        UpdateSortIcon();
        Reload();
    }

    // --- header sort arrow ---

    const int LVM_GETHEADER = 0x101F;
    const int HDM_GETITEMW = 0x120B;
    const int HDM_SETITEMW = 0x120C;
    const int HDI_FORMAT = 0x4;
    const int HDF_SORTUP = 0x400;
    const int HDF_SORTDOWN = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    struct HDITEM
    {
        public int mask, cxy;
        public nint pszText, hbm;
        public int cchTextMax, fmt;
        public nint lParam;
        public int iImage, iOrder, type;
        public nint pvFilter;
        public int state;
    }

    [DllImport("user32.dll")]
    static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern nint SendMessage(nint hWnd, int msg, nint wParam, ref HDITEM lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(nint hWnd, string app, string? idList);

    public void UpdateSortIcon()
    {
        if (!IsHandleCreated || _rows == null) return;
        nint header = SendMessage(Handle, LVM_GETHEADER, 0, 0);
        Col sortCol = _rows.Sort switch
        {
            SortColumn.Name => Col.Name,
            SortColumn.Length => Col.Size,
            SortColumn.Files => Col.Files,
            SortColumn.Modified => Col.Modified,
            SortColumn.Accessed => Col.Accessed,
            SortColumn.Delta => Col.Delta,
            _ => Col.OnDisk,
        };
        for (int i = 0; i < Columns.Count; i++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            SendMessage(header, HDM_GETITEMW, i, ref item);
            item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == (int)sortCol) item.fmt |= _rows.Descending ? HDF_SORTDOWN : HDF_SORTUP;
            SendMessage(header, HDM_SETITEMW, i, ref item);
        }
    }
}
