using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Deletion;
using DiskScope.Core.Native;
using DiskScope.Core.Scanning;

namespace DiskScope.UI;

/// <summary>File actions: open, Explorer, copy path, delete, rescan, the Windows context menu.</summary>
sealed partial class MainForm
{
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _miOpen = new() { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
    readonly ToolStripMenuItem _miReveal = new();
    readonly ToolStripMenuItem _miCopy = new() { ShortcutKeyDisplayString = "Ctrl+C" };
    readonly ToolStripMenuItem _miRescan = new() { ShortcutKeyDisplayString = "F5" };
    readonly ToolStripMenuItem _miRecycle = new() { ShortcutKeyDisplayString = "Del" };
    readonly ToolStripMenuItem _miDelete = new() { ShortcutKeyDisplayString = "Shift+Del" };
    readonly ToolStripMenuItem _miWindows = new();
    readonly ToolStripMenuItem _miAi = new() { ShortcutKeyDisplayString = "Ctrl+Shift+C" };
    readonly ToolStripMenuItem _miDuplicates = new();
    bool _rescanning;

    bool IsBusy => IsScanning || _rescanning || _saving || _loading;

    void BuildMenu()
    {
        _menu.Items.AddRange([_miOpen, _miReveal, _miCopy, _miAi, _miRescan, _miDuplicates, new ToolStripSeparator(), _miRecycle, _miDelete,
            new ToolStripSeparator(), _miWindows]);
        _miAi.Click += (_, _) => CopyAiPrompt(_tree.FocusedNode);
        _miOpen.Click += (_, _) => OpenSelected();
        _miReveal.Click += (_, _) => RevealSelected();
        _miCopy.Click += (_, _) => CopyPaths();
        _miRescan.Click += (_, _) => RescanSelected();
        _miDuplicates.Click += (_, _) => FindDuplicatesIn(SelectedTreeFolder());
        _miRecycle.Click += (_, _) => DeleteSelected(permanent: false);
        _miDelete.Click += (_, _) => DeleteSelected(permanent: true);
        _miWindows.Click += (_, _) => ShowWindowsMenu(_menuPoint, extended: false);
        _menu.Opening += (_, e) =>
        {
            _menuPoint = Cursor.Position;
            var nodes = _tree.SelectedNodes();
            if (nodes.Count == 0 || _rows == null)
            {
                e.Cancel = true;
                return;
            }
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                e.Cancel = true;
                BeginInvoke(() => ShowWindowsMenu(_menuPoint, extended: true));
                return;
            }
            bool single = nodes.Count == 1;
            bool isRoot = nodes.Contains(_rows.Store.Root);
            _miOpen.Enabled = _miAi.Enabled = single;
            _miRescan.Enabled = single && _rows.Store[nodes[0]].IsDirectory && !IsBusy;
            _miDuplicates.Enabled = single && _rows.Store[nodes[0]].IsDirectory;
            _miRecycle.Enabled = _miDelete.Enabled = !isRoot && !IsBusy;
            _miWindows.Enabled = !IsBusy;
        };
        _tree.ContextMenuStrip = _menu;
        _tree.DeleteRequested += permanent => DeleteSelected(permanent);
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.C && e.Control && e.Shift) CopyAiPrompt(_tree.FocusedNode);
            else if (e.KeyCode == Keys.C && e.Control) CopyPaths();
            else if (e.KeyCode == Keys.F5) RescanSelected();
            else return;
            e.Handled = true;
        };
    }

    Point _menuPoint;

    void ApplyMenuTexts()
    {
        _miOpen.Text = L.T("Open");
        _miReveal.Text = L.T("Reveal");
        _miCopy.Text = L.T("CopyPath");
        _miRescan.Text = L.T("Rescan");
        _miRecycle.Text = L.T("Recycle");
        _miDelete.Text = L.T("DeletePermanent");
        // Commands of Explorer's own menu run inside this process, so with administrator rights when elevated.
        _miWindows.Text = L.T("WindowsMenu") + (Elevation.IsElevated ? "  " + L.T("WindowsMenuElevated") : "") + "  (Shift+…)";
        _miAi.Text = L.T("CopyAiPrompt");
        _miDuplicates.Text = L.T("DupInFolder");
    }

    void CopyAiPrompt(int node)
    {
        if (_rows == null || node == NodeStore.None || !_rows.Store.IsLive(node)) return;
        Clipboard.SetText(AiPrompt.Build(_rows.Store, node));
        _status.Text = L.T("AiCopied");
    }

    protected override void WndProc(ref Message m)
    {
        if (Shell.HandleMenuMessage(ref m)) return;
        base.WndProc(ref m);
    }

    void OpenSelected() => OpenNode(_tree.FocusedNode);

    /// <summary>File types that run code when opened; opening them asks first, so a double-click cannot start one by accident.</summary>
    static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msix", ".msp", ".appx", ".appinstaller", ".lnk", ".url", ".jar", ".reg", ".cpl", ".msc", ".application", ".py", ".pyw",
    };

    void OpenNode(int node)
    {
        if (_rows == null || node == NodeStore.None) return;
        string path = _rows.Store.GetPath(node);
        // Windows would open a different item for "abc." (it becomes "abc"); better not to open anything.
        if (ShellOpen.HasUnsafeEnding(path))
        {
            MessageBox.Show(this, L.T("UnsafeNameOpen", path), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_rows.Store[node].IsDirectory && Executable.Contains(Path.GetExtension(path)) &&
            MessageBox.Show(this, L.T("ConfirmRunProgram", path) + (Elevation.IsElevated ? "\n\n" + L.T("RunsAsUser") : ""), Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            Process.Start(ShellOpen.StartInfo(path, Elevation.IsElevated));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OpenFile(int node) => OpenNode(node);

    void RevealSelected() => RevealNode(_tree.FocusedNode);

    void RevealNode(int node)
    {
        if (_rows != null && node != NodeStore.None) Shell.RevealInExplorer(_rows.Store.GetPath(node));
    }

    void CopyPaths() => CopyNodePaths(_tree.SelectedNodes());

    void CopyNodePaths(List<int> nodes)
    {
        if (_rows != null && nodes.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, nodes.Select(_rows.Store.GetPath)));
    }

    void DeleteSelected(bool permanent) => DeleteNodes(_tree.SelectedNodes(), permanent);

    /// <summary>Asks for confirmation, then puts the items in the delete queue (recycle bin unless permanent).</summary>
    void DeleteNodes(List<int> selected, bool permanent)
    {
        if (_rows == null) return;
        var store = _rows.Store;
        // Skip what is already queued, itself or through a queued folder above it.
        var nodes = TreeSync.TopLevel(store, selected.Where(n => store.IsLive(n) && !InQueuedFolder(store, n)));
        if (nodes.Count == 0) return;

        // Drive roots, system and program folders, profiles and their main folders are never deleted.
        // One real-path lookup per parent folder, not per item: emptying a big folder checks thousands at once.
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var refused = nodes.Where(n => _guard.CheckResolved(store.GetPath(n), resolved) != DeletionGuard.Verdict.Allowed).ToList();
        if (refused.Count > 0)
        {
            MessageBox.Show(this, L.T("DeleteProtected", string.Join("\n", refused.Take(10).Select(store.GetPath))), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            nodes = nodes.Except(refused).ToList();
            if (nodes.Count == 0) return;
        }

        long size = TreeSync.TotalSize(store, nodes);
        var paths = nodes.Select(store.GetPath).ToList();
        string list = string.Join("\n", paths.Take(10)) + (paths.Count > 10 ? "\n" + L.T("AndMore", paths.Count - 10) : "");
        string question = L.T(permanent ? "ConfirmDelete" : "ConfirmRecycle", nodes.Count, Format.Size(size, L.Culture), list);
        // A saved scan may be days old: what is in those folders now may differ from what the tree shows.
        if (_session == null && _loaded != null && (DateTime.UtcNow - _loaded.ScanTimeUtc).TotalHours > 24)
            question += "\n\n" + L.T("DeleteFromOldScan", _loaded.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture));
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        EnqueueDelete(nodes, permanent);
    }

    readonly DeletionGuard _guard = new();

    static bool InQueuedFolder(NodeStore store, int node)
    {
        for (int i = node; i != NodeStore.None; i = store[i].Parent)
            if ((store[i].Flags & NodeFlags.Queued) != 0) return true;
        return false;
    }

    /// <summary>Test hook: deletes through the queue, waits for it, and returns the paths that are still there.</summary>
    internal List<string> PerformDelete(List<int> nodes, bool permanent)
    {
        var jobs = EnqueueDelete(nodes, permanent);
        _queue.WaitIdle(TimeSpan.FromMinutes(5));
        DrainJobUpdates();
        return jobs.Where(j => j.State != Core.Deletion.DeleteState.Done && !j.IsDirectory).Select(j => j.Path).ToList();
    }

    void RescanSelected()
    {
        if (_rows == null || IsBusy) return;
        int node = _tree.FocusedNode;
        if (node == NodeStore.None) return;
        if (!_rows.Store[node].IsDirectory) node = _rows.Store[node].Parent;
        Rescan(node);
    }

    readonly Queue<(NodeStore Store, int Dir)> _rescanQueue = new();

    /// <summary>Rescans a folder in the background; requests arriving meanwhile wait their turn.</summary>
    async void Rescan(int dir)
    {
        if (_rows == null) return;
        _rescanQueue.Enqueue((_rows.Store, dir));
        if (_rescanning) return;
        _rescanning = true;
        UpdateButtons();
        _timer.Start();
        try
        {
            while (_rescanQueue.TryDequeue(out var next))
            {
                if (IsDisposed) break;
                if (next.Store != _rows?.Store || !next.Store.IsLive(next.Dir)) continue;
                string path = next.Store.GetPath(next.Dir);
                try
                {
                    await Task.Run(() => new DirectoryScanner().Rescan(next.Store, next.Dir, new ScanProgress(), CancellationToken.None));
                }
                catch (ScanSourceException)
                {
                    // The tree keeps what it had; the user learns why nothing changed.
                    if (!IsDisposed) _status.Text = L.T("RescanUnavailable", path);
                }
            }
        }
        finally
        {
            _rescanning = false;
            if (!IsDisposed) // the window may have closed while rescanning
            {
                if (!IsScanning) _timer.Stop();
                Tick();
                UpdateButtons();
                UpdateDetail();
                RefreshSearchAfterChange();
            }
        }
    }

    void ShowWindowsMenu(Point screen, bool extended)
    {
        if (_rows == null || IsBusy) return;
        var store = _rows.Store;
        int focused = _tree.FocusedNode;
        if (focused == NodeStore.None) return;
        // The shell menu works on items of one folder: those sharing the focused item's parent.
        int parent = store[focused].Parent;
        var nodes = _tree.SelectedNodes().Where(n => store[n].Parent == parent).ToList();
        if (!nodes.Contains(focused)) nodes = [focused];
        if (!Shell.ShowContextMenu(this, nodes.Select(store.GetPath).ToList(), screen, extended)) return;

        // The command may have deleted or renamed items; give the shell a moment, then reconcile.
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (IsBusy || _rows?.Store != store) return;
            TreeSync.RemoveVanished(store, nodes);
            _tree.Reload();
            UpdateDetail();
        };
        timer.Start();
    }
}
