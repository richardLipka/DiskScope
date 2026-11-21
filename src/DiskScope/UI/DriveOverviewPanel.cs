using System.ComponentModel;
using DiskScope.Core;
using DiskScope.Core.Storage;

namespace DiskScope.UI;

/// <summary>
/// The start page: every drive with how full it is, when it was last scanned and how much more it holds since.
/// Double-click opens the drive's last scan, or scans it when there is none.
/// </summary>
sealed class DriveOverviewPanel : UserControl
{
    sealed record DriveRow(string Root, string Label, long Used, long Free, long Total, SnapshotInfo? LastScan, long? Change);

    readonly Label _hint = new() { Dock = DockStyle.Top, AutoSize = false, Padding = new Padding(10, 8, 10, 4) };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        BorderStyle = BorderStyle.None, OwnerDraw = true,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 6, 6) };
    readonly Button _open = new() { AutoSize = true };
    readonly Button _scan = new() { AutoSize = true };
    readonly Button _refresh = new() { AutoSize = true };
    readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SnapshotCatalog? Catalog { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DriveUsageLog? UsageLog { get; set; }

    /// <summary>Open the last saved scan of a drive.</summary>
    public event Action<SnapshotInfo>? OpenRequested;

    /// <summary>Scan a drive.</summary>
    public event Action<string>? ScanRequested;

    public DriveOverviewPanel()
    {
        _bar.Controls.AddRange([_open, _scan, _refresh]);
        Controls.Add(_list);
        Controls.Add(_hint);
        Controls.Add(_bar);
        foreach (var w in new[] { 300, 220, 100, 100, 100, 230, 140 }) _list.Columns.Add("", w);
        for (int i = 2; i <= 4; i++) _list.Columns[i].TextAlign = HorizontalAlignment.Right;
        _list.Columns[6].TextAlign = HorizontalAlignment.Right;
        _list.DrawColumnHeader += (_, e) => Theme.DrawHeader(e, _list.Font);
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawSubItem;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Activate();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            Activate();
            e.Handled = e.SuppressKeyPress = true;
        };
        _open.Click += (_, _) =>
        {
            if (Selected?.LastScan is SnapshotInfo s) OpenRequested?.Invoke(s);
        };
        _scan.Click += (_, _) =>
        {
            if (Selected is DriveRow d) ScanRequested?.Invoke(d.Root);
        };
        _refresh.Click += (_, _) => Fill();
        ApplyTexts();
    }

    DriveRow? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as DriveRow : null;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
        _hint.Height = LogicalToDeviceUnits(34);
        int size = LogicalToDeviceUnits(24);
        _icons.ImageSize = new Size(size, size);
        _list.SmallImageList = _icons; // also sets a comfortable row height
    }

    public void ApplyTexts()
    {
        _hint.Text = L.T("OverviewHint");
        _open.Text = L.T("OverviewOpen");
        _scan.Text = L.T("Scan");
        _refresh.Text = L.T("OverviewRefresh");
        string[] cols = ["OverviewColDrive", "OverviewColUsage", "OverviewColUsed", "OverviewColFree", "OverviewColTotal", "OverviewColLastScan", "OverviewColChange"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        if (_list.Items.Count > 0) Fill();
    }

    /// <summary>Reads the drives again and records how full each one is now.</summary>
    public void Fill(string? select = null)
    {
        select ??= Selected?.Root;
        var scans = Catalog?.List() ?? [];
        var rows = new List<DriveRow>();
        var now = DateTime.UtcNow;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                string root = d.RootDirectory.FullName;
                long used = d.TotalSize - d.TotalFreeSpace;
                var last = scans.FirstOrDefault(s => string.Equals(s.RootPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                long? change = last != null && UsageLog?.At(root, last.ScanTimeUtc) is DriveUsageLog.Entry then ? used - then.Used : null;
                UsageLog?.Record(root, used, d.TotalSize, now);
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? L.T("LocalDisk") : d.VolumeLabel;
                rows.Add(new DriveRow(root, $"{d.Name.TrimEnd('\\')}  {label} ({d.DriveFormat})", used, d.AvailableFreeSpace, d.TotalSize, last, change));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The drive went away while being read: leave it out.
            }
        }
        UsageLog?.Save();

        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();
        foreach (var row in rows)
        {
            if (ShellIcons.Drive(row.Root) is Icon icon) _icons.Images.Add(row.Root, icon);
            string lastScan = row.LastScan == null ? L.T("OverviewNever") : LastScanText(row.LastScan);
            string change = row.Change is long c ? (c == 0 ? "0" : Format.SignedSize(c, L.Culture)) : "";
            var item = new ListViewItem([row.Label, "", Format.Size(row.Used, L.Culture), Format.Size(row.Free, L.Culture),
                Format.Size(row.Total, L.Culture), lastScan, change]) { Tag = row, ImageKey = row.Root };
            _list.Items.Add(item);
            if (string.Equals(row.Root, select, StringComparison.OrdinalIgnoreCase)) item.Selected = item.Focused = true;
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = _list.Items[0].Focused = true;
        _list.EndUpdate();
        UpdateButtons();
    }

    static string LastScanText(SnapshotInfo s)
    {
        int days = (DateTime.Today - s.ScanTimeUtc.ToLocalTime().Date).Days;
        string age = days == 0 ? L.T("Today") : L.T("DaysAgo", days);
        return $"{s.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture)} ({age})" + (s.Complete ? "" : "  " + L.T("Incomplete"));
    }

    void UpdateButtons()
    {
        _open.Enabled = Selected?.LastScan != null;
        _scan.Enabled = Selected != null;
    }

    void Activate()
    {
        if (Selected is not DriveRow d) return;
        if (d.LastScan != null) OpenRequested?.Invoke(d.LastScan);
        else ScanRequested?.Invoke(d.Root);
    }

    public void FocusList() => _list.Focus();

    void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.Item?.Tag is not DriveRow row)
        {
            e.DrawDefault = true;
            return;
        }
        var g = e.Graphics;
        bool selected = e.Item.Selected;
        using (var back = new SolidBrush(selected ? (_list.Focused ? SystemColors.Highlight : SystemColors.ControlLight) : _list.BackColor))
            g.FillRectangle(back, e.Bounds);
        var track = Rectangle.Inflate(e.Bounds, -LogicalToDeviceUnits(6), -LogicalToDeviceUnits(6));
        double share = row.Total > 0 ? (double)row.Used / row.Total : 0;
        using (var br = new SolidBrush(Theme.BarTrack)) g.FillRectangle(br, track);
        // Red when nearly full, so a drive that needs attention stands out.
        var fill = share >= 0.9 ? Theme.Grow : Theme.Bar;
        using (var br = new SolidBrush(fill)) g.FillRectangle(br, track with { Width = (int)Math.Round(track.Width * Math.Clamp(share, 0, 1)) });
        TextRenderer.DrawText(g, (share * 100).ToString("0", L.Culture) + " %", Font, e.Bounds, Theme.BarText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    internal void ActivateFirstForTest() => Activate();
}
