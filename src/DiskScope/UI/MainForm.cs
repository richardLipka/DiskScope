using System.ComponentModel;
using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Native;
using DiskScope.Core.Scanning;

namespace DiskScope.UI;

sealed partial class MainForm : Form
{
    readonly AppSettings _settings;
    readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
    readonly ToolStripComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false };
    readonly ToolStripButton _browse = new();
    readonly ToolStripButton _scan = new() { Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    readonly ToolStripButton _stop = new() { Enabled = false };
    readonly ToolStripButton _up = new();
    readonly ToolStripButton _admin = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripDropDownButton _language = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStrip _breadcrumb = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 1, 6, 1), RenderMode = ToolStripRenderMode.System };
    readonly TreeListView _tree = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly Label _detail = new() { Dock = DockStyle.Bottom, AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft };
    readonly StatusStrip _statusBar = new() { SizingGrip = true };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee };
    readonly ToolStripStatusLabel _mode = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };

    ScanSession? _session;
    TreeRows? _rows;
    int _shownVersion = -1;
    string? _customFolder;

    public MainForm(AppSettings settings, string? startPath, bool autoScan)
    {
        _settings = settings;
        Font = SystemFonts.MessageBoxFont!;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Bounds = settings.WindowBounds is Rectangle r && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))
            ? r : new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 10, area.Width * 4 / 5, area.Height * 4 / 5);
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
        KeyPreview = true;

        BuildToolbar();
        BuildColumns();
        BuildMenu();
        _detail.Height = LogicalToDeviceUnits(26);
        _detail.BackColor = SystemColors.Window;
        _statusBar.Items.AddRange([_status, _progress, _mode]);

        Controls.Add(_tree);
        Controls.Add(_detail);
        Controls.Add(_breadcrumb);
        Controls.Add(_toolbar);
        Controls.Add(_statusBar);

        _tree.SelectedIndexChanged += (_, _) => UpdateDetail();
        _tree.VirtualItemsSelectionRangeChanged += (_, _) => UpdateDetail();
        _tree.NodeActivated += ActivateNode;
        _tree.NavigateUp += GoUp;
        _timer.Tick += (_, _) => Tick();

        L.Changed += ApplyTexts;
        ApplyTexts();
        if (startPath != null) SelectTarget(startPath);
        else if (settings.LastPath != null) SelectTarget(settings.LastPath);
        Load += (_, _) =>
        {
            if (autoScan) StartScan();
            else LoadLastOnStart();
        };
    }

    internal NodeStore? Store => _rows?.Store;

    internal void ShowMenuAtFocused()
    {
        if (_tree.FocusedItem == null) return;
        var r = _tree.GetItemRect(_tree.FocusedItem.Index);
        _menu.Show(_tree, new Point(r.X + LogicalToDeviceUnits(200), r.Bottom));
    }

    /// <summary>Expands the largest child repeatedly, a given number of levels deep.</summary>
    public void ExpandLargest(int levels)
    {
        if (_rows == null) return;
        int node = _rows.ViewRoot;
        for (int i = 0; i < levels; i++)
        {
            var children = _rows.SortedChildren(node);
            int next = Array.FindIndex(children, c => _rows.HasChildren(c)) is int k && k >= 0 ? children[k] : NodeStore.None;
            if (next == NodeStore.None) break;
            _rows.SetExpanded(next, true);
            node = next;
        }
        _tree.Reload();
        _tree.SelectNode(node);
        _tree.Focus();
    }

    void BuildToolbar()
    {
        _target.Width = LogicalToDeviceUnits(420);
        _target.DropDown += (_, _) => FillTargets();
        _browse.Click += (_, _) => BrowseFolder();
        _scan.Click += (_, _) => StartScan();
        _stop.Click += (_, _) => _session?.Cancel();
        _up.Click += (_, _) => GoUp();
        _admin.Click += (_, _) => RestartElevated();
        _admin.Visible = !Elevation.IsElevated;
        foreach (string lang in L.Languages)
        {
            var item = new ToolStripMenuItem(lang == "cs" ? "Čeština" : "English") { Tag = lang };
            item.Click += (_, _) => L.Lang = lang;
            _language.DropDownItems.Add(item);
        }
        BuildHistory();
        _toolbar.Items.AddRange([_target, _browse, new ToolStripSeparator(), _scan, _stop, new ToolStripSeparator(), _history,
            new ToolStripSeparator(), _up, _language, _admin]);
        FillTargets();
    }

    void BuildColumns()
    {
        // Widths are kept in 96-DPI units so they survive moving between monitors.
        int[] widths = [420, 95, 150, 95, 95, 85, 150];
        if (_settings.ColumnWidths is { Length: 7 } saved) widths = saved;
        foreach (int w in widths) _tree.Columns.Add("", LogicalToDeviceUnits(w), HorizontalAlignment.Right);
        _tree.Columns[0].TextAlign = HorizontalAlignment.Left;
        _tree.Columns[(int)Col.Percent].TextAlign = HorizontalAlignment.Center;
        _tree.Columns[(int)Col.Modified].TextAlign = HorizontalAlignment.Left;
    }

    void ApplyTexts()
    {
        Text = L.T("AppTitle");
        _browse.Text = L.T("Browse");
        _browse.ToolTipText = L.T("BrowseTip");
        _scan.Text = L.T("Scan");
        _stop.Text = L.T("Stop");
        _up.Text = "↑ " + L.T("Up");
        _up.ToolTipText = L.T("UpTip");
        _admin.Text = "🛡 " + L.T("RunAsAdmin");
        _admin.ToolTipText = L.T("RunAsAdminTip");
        _language.Text = L.T("Language") + ": " + L.Lang.ToUpperInvariant();
        foreach (ToolStripMenuItem item in _language.DropDownItems) item.Checked = (string)item.Tag! == L.Lang;
        string[] cols = ["ColName", "ColOnDisk", "ColPercent", "ColSize", "ColFiles", "ColFolders", "ColModified", "ColDelta"];
        for (int i = 0; i < _tree.Columns.Count; i++) _tree.Columns[i].Text = L.T(cols[i]);
        _tree.UpdateSortIcon();
        ApplyMenuTexts();
        ApplyHistoryTexts();
        string? selected = SelectedTarget;
        FillTargets();
        if (selected != null) SelectTarget(selected);
        UpdateStatus();
        UpdateDetail();
        _tree.Invalidate();
    }

    // --- scan targets ---

    sealed record Target(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    string? SelectedTarget => (_target.SelectedItem as Target)?.Path;

    void FillTargets()
    {
        string? selected = SelectedTarget;
        _target.Items.Clear();
        if (_customFolder != null) _target.Items.Add(new Target(_customFolder, "📁 " + _customFolder));
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
            string label = string.IsNullOrEmpty(d.VolumeLabel) ? L.T("LocalDisk") : d.VolumeLabel;
            _target.Items.Add(new Target(d.RootDirectory.FullName, L.T("DriveItem", d.Name, label, d.DriveFormat,
                Format.Size(d.AvailableFreeSpace, L.Culture), Format.Size(d.TotalSize, L.Culture))));
        }
        if (selected != null) SelectTarget(selected, refill: false);
        if (_target.SelectedIndex < 0 && _target.Items.Count > 0) _target.SelectedIndex = 0;
    }

    void SelectTarget(string path, bool refill = true)
    {
        path = ScanSession.NormalizeRoot(path);
        bool isDrive = path.Length == 3;
        if (!isDrive && !string.Equals(_customFolder, path, StringComparison.OrdinalIgnoreCase))
        {
            _customFolder = path;
            if (refill) FillTargets();
        }
        foreach (Target t in _target.Items)
            if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _target.SelectedItem = t;
                return;
            }
    }

    void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = L.T("BrowseTip"), SelectedPath = SelectedTarget ?? "" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SelectTarget(dlg.SelectedPath);
    }

    // --- scanning ---

    bool IsScanning => _session?.State == ScanState.Running;

    void StartScan()
    {
        string? path = SelectedTarget;
        if (path == null || IsBusy) return;
        _settings.LastPath = path;
        _loaded = null;
        _session = new ScanSession(path);
        _session.Finished += s => BeginInvoke(() => OnScanFinished(s));
        _session.Start();
        ShowStore(_session.Store);
        _timer.Start();
        UpdateButtons();
    }

    void ShowStore(NodeStore store)
    {
        _rows = new TreeRows(store);
        _tree.Rows = _rows;
        _shownVersion = store.Version;
        UpdateBreadcrumb();
        UpdateStatus();
        UpdateDetail();
    }

    void OnScanFinished(ScanSession session)
    {
        if (session != _session) return;
        _timer.Stop();
        Tick();
        UpdateButtons();
        if (session.State == ScanState.Failed)
            MessageBox.Show(this, L.T("ScanFailed", session.Error?.Message ?? ""), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        if (session.State == ScanState.Completed && _settings.AutoSave) SaveSnapshot(session);
        ScanCompleted?.Invoke();
    }

    /// <summary>Raised after a scan ends and the view is refreshed; used by the test hooks.</summary>
    public event Action? ScanCompleted;

    void Tick()
    {
        if (_rows != null && _rows.Store.Version != _shownVersion)
        {
            _shownVersion = _rows.Store.Version;
            _tree.Reload();
            UpdateDetail();
        }
        UpdateStatus();
    }

    void UpdateButtons()
    {
        _scan.Enabled = !IsBusy;
        _stop.Enabled = IsScanning;
        _target.Enabled = !IsBusy;
        _browse.Enabled = !IsBusy;
    }

    void UpdateStatus()
    {
        if (_session == null)
        {
            _status.Text = _loaded != null && _rows != null ? LoadedStatusLine(_loaded) : L.T("Ready");
            _progress.Visible = false;
            _mode.Text = _rows != null ? L.T("Memory", Format.Size(_rows.Store.MemoryUsage, L.Culture)) : "";
            return;
        }
        var p = _session.Progress;
        string state = _session.State switch
        {
            ScanState.Running => L.T("Scanning"),
            ScanState.Completed => L.T("ScanDone"),
            ScanState.Cancelled => L.T("ScanCancelled"),
            _ => L.T("ScanFailed", ""),
        };
        if (_session.State == ScanState.Running && p.TotalRecords > 0)
            state += " · " + L.T("MftProgress", p.Records * 100 / p.TotalRecords);
        var root = _session.Store[_session.Store.Root];
        _status.Text = L.T("StatusLine", state, Format.Count(root.FileCount, L.Culture), Format.Count(root.DirCount, L.Culture),
            Format.Size(root.Size, L.Culture), Format.Duration(_session.Elapsed), Format.Count(p.Errors, L.Culture));
        if (_session.State == ScanState.Running && p.CurrentPath is string cur) _status.Text += "  ·  " + cur;
        if (_session.MftError is Exception mftError) _status.Text += "  ·  " + L.T("MftFallback", mftError.Message);
        _progress.Visible = _session.State == ScanState.Running;
        if (p.TotalRecords > 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)Math.Min(100, p.Records * 100 / p.TotalRecords);
        }
        else _progress.Style = ProgressBarStyle.Marquee;
        _mode.Text = (_session.Mode == ScanMode.Mft ? L.T("ModeMft") : L.T("ModeDir")) + " · " +
                     L.T("Memory", Format.Size(_session.Store.MemoryUsage, L.Culture));
    }

    void UpdateDetail()
    {
        if (_rows == null)
        {
            _detail.Text = "";
            return;
        }
        int node = _tree.FocusedNode;
        if (node == NodeStore.None)
        {
            _detail.Text = "";
            return;
        }
        var s = _rows.Store;
        ref var n = ref s[node];
        _detail.Text = n.IsDirectory
            ? L.T("DetailDir", s.GetPath(node), Format.Size(n.Size, L.Culture), Format.Size(n.Length, L.Culture),
                Format.Count(n.FileCount, L.Culture), Format.Count(n.DirCount, L.Culture))
            : L.T("DetailFile", s.GetPath(node), Format.Size(n.Size, L.Culture), Format.Size(n.Length, L.Culture),
                n.MTime > 0 ? DateTime.FromFileTimeUtc(n.MTime).ToLocalTime().ToString("g", L.Culture) : "");
    }

    // --- navigation ---

    void ActivateNode(int node)
    {
        if (_rows == null) return;
        if (_rows.Store[node].IsDirectory) ZoomTo(node);
        else OpenFile(node);
    }

    void ZoomTo(int node, int select = NodeStore.None)
    {
        if (_rows == null) return;
        _rows.ViewRoot = node;
        _tree.Reload();
        UpdateBreadcrumb();
        if (select != NodeStore.None) _tree.SelectNode(select);
        else _tree.SelectRow(_rows.Count > 1 ? 1 : 0);
    }

    void GoUp()
    {
        if (_rows == null) return;
        int root = _rows.ViewRoot;
        int parent = _rows.Store[root].Parent;
        if (parent != NodeStore.None) ZoomTo(parent, root);
    }

    void UpdateBreadcrumb()
    {
        _breadcrumb.SuspendLayout();
        _breadcrumb.Items.Clear();
        if (_rows != null && _rows.Store.Root != NodeStore.None)
        {
            var chain = new List<int>();
            for (int i = _rows.ViewRoot; i != NodeStore.None; i = _rows.Store[i].Parent) chain.Add(i);
            chain.Reverse();
            foreach (int node in chain)
            {
                if (_breadcrumb.Items.Count > 0) _breadcrumb.Items.Add(new ToolStripLabel("›") { ForeColor = SystemColors.GrayText });
                var b = new ToolStripButton(_rows.Store.GetName(node)) { Tag = node };
                if (node == _rows.ViewRoot) b.Font = new Font(b.Font, FontStyle.Bold);
                b.Click += (_, _) => ZoomTo((int)b.Tag!);
                _breadcrumb.Items.Add(b);
            }
        }
        _breadcrumb.ResumeLayout();
        _up.Enabled = _rows != null && _rows.Store.Root != NodeStore.None && _rows.ViewRoot != _rows.Store.Root;
    }

    void RestartElevated()
    {
        string args = SelectedTarget is string p ? $"--path \"{p.TrimEnd('\\')}\"" : "";
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            return; // UAC prompt declined
        }
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsScanning && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, L.T("ConfirmStopScan"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _session?.Cancel();
        // Let a snapshot that is being written finish rather than leave a broken file behind.
        try { _saveTask?.Wait(); } catch { }
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.WindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.ColumnWidths = _tree.Columns.Cast<ColumnHeader>().Take(7).Select(c => c.Width * 96 / DeviceDpi).ToArray();
        _settings.Language = L.Lang;
        _settings.Save();
        base.OnFormClosing(e);
    }
}
