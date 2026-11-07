using System.Diagnostics;
using DiskScope.Core;
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
    bool _rescanning;

    bool IsBusy => IsScanning || _rescanning || _saving || _loading;

    void BuildMenu()
    {
        _menu.Items.AddRange([_miOpen, _miReveal, _miCopy, _miRescan, new ToolStripSeparator(), _miRecycle, _miDelete,
            new ToolStripSeparator(), _miWindows]);
        _miOpen.Click += (_, _) => OpenSelected();
        _miReveal.Click += (_, _) => RevealSelected();
        _miCopy.Click += (_, _) => CopyPaths();
        _miRescan.Click += (_, _) => RescanSelected();
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
            _miOpen.Enabled = single;
            _miRescan.Enabled = single && _rows.Store[nodes[0]].IsDirectory && !IsBusy;
            _miRecycle.Enabled = _miDelete.Enabled = !isRoot && !IsBusy;
            _miWindows.Enabled = !IsBusy;
        };
        _tree.ContextMenuStrip = _menu;
        _tree.DeleteRequested += permanent => DeleteSelected(permanent);
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.C && e.Control) CopyPaths();
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
        _miWindows.Text = L.T("WindowsMenu") + "  (Shift+…)";
    }

    protected override void WndProc(ref Message m)
    {
        if (Shell.HandleMenuMessage(ref m)) return;
        base.WndProc(ref m);
    }

    List<string> SelectedPaths() => _rows == null ? [] : _tree.SelectedNodes().Select(_rows.Store.GetPath).ToList();

    void OpenSelected()
    {
        if (_rows == null || _tree.FocusedNode is var node && node == NodeStore.None) return;
        string path = _rows.Store.GetPath(node);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OpenFile(int node)
    {
        _tree.SelectNode(node);
        OpenSelected();
    }

    void RevealSelected()
    {
        if (_rows != null && _tree.FocusedNode is var node && node != NodeStore.None)
            Shell.RevealInExplorer(_rows.Store.GetPath(node));
    }

    void CopyPaths()
    {
        var paths = SelectedPaths();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    void DeleteSelected(bool permanent)
    {
        if (_rows == null) return;
        if (IsBusy)
        {
            MessageBox.Show(this, L.T("WaitForScan"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var store = _rows.Store;
        var nodes = TreeSync.TopLevel(store, _tree.SelectedNodes());
        if (nodes.Count == 0) return;

        long size = TreeSync.TotalSize(store, nodes);
        var paths = nodes.Select(store.GetPath).ToList();
        string list = string.Join("\n", paths.Take(10)) + (paths.Count > 10 ? "\n" + L.T("AndMore", paths.Count - 10) : "");
        string question = L.T(permanent ? "ConfirmDelete" : "ConfirmRecycle", nodes.Count, Format.Size(size, L.Culture), list);
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var failed = PerformDelete(nodes, permanent);
        if (failed.Count > 0)
            MessageBox.Show(this, L.T("DeleteFailed", string.Join("\n", failed.Take(10))), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Deletes without asking and updates the tree; returns the paths of files that are still there.</summary>
    internal List<string> PerformDelete(List<int> nodes, bool permanent)
    {
        var store = _rows!.Store;
        // Keep the selection near where it was: on the row that follows the first deleted one.
        int row = _rows.IndexOf(nodes[0]);
        Shell.Delete(this, nodes.Select(store.GetPath), permanent);
        long before = store[store.Root].Size;
        var remaining = TreeSync.RemoveVanished(store, nodes);
        _tree.Reload();
        _tree.SelectRow(Math.Min(row, _rows.Count - 1));
        _status.Text = L.T("Freed", Format.Size(before - store[store.Root].Size, L.Culture));
        UpdateDetail();

        // A folder that was only partly deleted is rescanned so its size is right again.
        foreach (int dir in remaining.Where(n => store[n].IsDirectory)) Rescan(dir);
        return remaining.Where(n => !store[n].IsDirectory).Select(store.GetPath).ToList();
    }

    void RescanSelected()
    {
        if (_rows == null || IsBusy) return;
        int node = _tree.FocusedNode;
        if (node == NodeStore.None) return;
        if (!_rows.Store[node].IsDirectory) node = _rows.Store[node].Parent;
        Rescan(node);
    }

    async void Rescan(int dir)
    {
        if (_rows == null || _rescanning) return;
        var store = _rows.Store;
        _rescanning = true;
        UpdateButtons();
        _timer.Start();
        try
        {
            await Task.Run(() => new DirectoryScanner().Rescan(store, dir, new ScanProgress(), CancellationToken.None));
        }
        finally
        {
            _rescanning = false;
            if (!IsScanning) _timer.Stop();
            Tick();
            UpdateButtons();
            UpdateDetail();
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
