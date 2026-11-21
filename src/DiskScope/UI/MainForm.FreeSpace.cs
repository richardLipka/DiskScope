using DiskScope.Core;
using DiskScope.Core.Deletion;
using DiskScope.Core.Native;

namespace DiskScope.UI;

/// <summary>Freeing space without deleting: moving to another drive, compressing, compacting virtual disks.</summary>
sealed partial class MainForm
{
    readonly ToolStripMenuItem _miMove = new();
    readonly ToolStripMenuItem _miCompress = new();
    readonly ToolStripMenuItem _miCompact = new();
    bool _compacting;

    static readonly CompressionKind[] CompressionChoices = [CompressionKind.Lzx, CompressionKind.Xpress16K, CompressionKind.Xpress8K, CompressionKind.Xpress4K];

    void BuildFreeSpaceMenu()
    {
        _miMove.Click += (_, _) => MoveNodes(_tree.SelectedNodes());
        _miCompress.Click += (_, _) => CompressNodes(_tree.SelectedNodes());
        _miCompact.Click += (_, _) => CompactVirtualDisk(_tree.FocusedNode);
    }

    void ApplyFreeSpaceTexts()
    {
        _miMove.Text = L.T("MoveItem");
        _miCompress.Text = L.T("CompressItem");
        _miCompact.Text = L.T("VdiskItem");
    }

    /// <summary>Enables the items for the current selection; called when the tree's menu opens.</summary>
    void UpdateFreeSpaceMenu(List<int> nodes, bool isRoot)
    {
        _miMove.Enabled = !isRoot && !IsBusy;
        _miCompress.Enabled = !IsBusy;
        bool vdisk = nodes.Count == 1 && !_rows!.Store[nodes[0]].IsDirectory && VirtualDisk.IsVirtualDisk(_rows.Store.GetName(nodes[0]));
        _miCompact.Visible = vdisk;
        _miCompact.Enabled = vdisk && !IsBusy && !_compacting;
    }

    /// <summary>The selection without items already queued and without items inside other selected folders.</summary>
    List<int> ActionableNodes(List<int> selected)
    {
        var store = _rows!.Store;
        return TreeSync.TopLevel(store, selected.Where(n => store.IsLive(n) && !InQueuedFolder(store, n)));
    }

    void MoveNodes(List<int> selected)
    {
        if (_rows == null || IsBusy) return;
        var store = _rows.Store;
        var nodes = ActionableNodes(selected).Where(n => n != store.Root).ToList();
        if (nodes.Count == 0) return;
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var refused = nodes.Where(n => _guard.CheckResolved(store.GetPath(n), resolved) != DeletionGuard.Verdict.Allowed).ToList();
        if (refused.Count > 0)
        {
            ShowError(L.T("MoveProtected", string.Join("\n", refused.Take(10).Select(store.GetPath))));
            nodes = nodes.Except(refused).ToList();
            if (nodes.Count == 0) return;
        }

        using var pick = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = L.T("MovePick"), SelectedPath = _settings.LastMoveFolder ?? "" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string destination = pick.SelectedPath;
        _settings.LastMoveFolder = destination;

        long size = TreeSync.TotalSize(store, nodes);
        string sourceRoot = Path.GetPathRoot(store.GetPath(nodes[0])) ?? "";
        bool sameDrive = string.Equals(sourceRoot, Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);
        long free = 0;
        try { free = new DriveInfo(destination).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        if (!sameDrive && free < size)
        {
            ShowError(L.T("MoveNoSpace", Format.Size(size, L.Culture), destination, Format.Size(free, L.Culture)));
            return;
        }

        bool anyFolder = nodes.Any(n => store[n].IsDirectory);
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = L.T("MoveHeading", nodes.Count, Format.Size(size, L.Culture), destination),
            Text = (sameDrive ? L.T("MoveSameDrive") : L.T("MoveText", Format.Size(free, L.Culture))) + "\n\n" +
                   string.Join("\n", nodes.Take(8).Select(store.GetPath)) + (nodes.Count > 8 ? "\n" + L.T("AndMore", nodes.Count - 8) : ""),
            Icon = sameDrive ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            Verification = anyFolder ? new TaskDialogVerificationCheckBox(L.T("MoveLeaveLink")) : null,
            Buttons = { TaskDialogButton.OK, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
        };
        if (TaskDialog.ShowDialog(this, page) != TaskDialogButton.OK) return;
        EnqueueJobs(nodes, DeleteMethod.Move, destination, page.Verification?.Checked == true, default);
    }

    void CompressNodes(List<int> selected)
    {
        if (_rows == null || IsBusy) return;
        var store = _rows.Store;
        var nodes = ActionableNodes(selected);
        if (nodes.Count == 0) return;
        var refused = nodes.Where(n => _guard.CheckCompress(store.GetPath(n)) != DeletionGuard.Verdict.Allowed).ToList();
        if (refused.Count > 0)
        {
            ShowError(L.T("CompressProtected", string.Join("\n", refused.Take(10).Select(store.GetPath))));
            nodes = nodes.Except(refused).ToList();
            if (nodes.Count == 0) return;
        }
        var buttons = CompressionChoices.Select(k => new TaskDialogRadioButton(L.T("Compress" + k)) { Tag = k }).ToList();
        int remembered = Array.IndexOf(CompressionChoices, _settings.Compression);
        buttons[remembered >= 0 ? remembered : 0].Checked = true;
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = L.T("CompressHeading", nodes.Count, Format.Size(TreeSync.TotalSize(store, nodes), L.Culture)),
            Text = L.T("CompressText"),
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK, TaskDialogButton.Cancel },
        };
        foreach (var b in buttons) page.RadioButtons.Add(b);
        if (TaskDialog.ShowDialog(this, page) != TaskDialogButton.OK) return;
        var kind = (CompressionKind)page.RadioButtons.First(b => b.Checked).Tag!;
        _settings.Compression = kind;
        EnqueueJobs(nodes, DeleteMethod.Compress, null, false, kind);
    }

    async void CompactVirtualDisk(int node)
    {
        if (_rows == null || node == NodeStore.None || IsBusy || _compacting) return;
        var store = _rows.Store;
        string path = store.GetPath(node);
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = L.T("VdiskHeading", Path.GetFileName(path), Format.Size(store[node].Size, L.Culture)),
            Text = L.T("VdiskText", path) + (Elevation.IsElevated ? "" : "\n\n" + L.T("VdiskElevation")),
            Icon = TaskDialogIcon.Information,
            Verification = new TaskDialogVerificationCheckBox(L.T("VdiskShutdownWsl"), VirtualDisk.LooksLikeWsl(path)),
            Buttons = { TaskDialogButton.OK, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
        };
        if (TaskDialog.ShowDialog(this, page) != TaskDialogButton.OK) return;
        _compacting = true;
        _status.Text = L.T("VdiskRunning", path);
        string message;
        try
        {
            var (exit, output, before, after) = await VirtualDisk.CompactAsync(path, page.Verification.Checked, Elevation.IsElevated);
            message = exit == 0
                ? L.T("VdiskDone", Format.Size(before, L.Culture), Format.Size(after, L.Culture), Format.Size(Math.Max(0, before - after), L.Culture))
                : L.T("VdiskFailed", exit, output.Trim());
            if (exit == 0) _freedThisSession += Math.Max(0, before - after);
        }
        catch (Exception ex)
        {
            message = L.T("VdiskFailed", "-", ex.Message); // e.g. the elevation prompt was declined
        }
        finally
        {
            _compacting = false;
        }
        if (IsDisposed) return;
        if (_rows?.Store == store && store.IsLive(node) && UpdateFileSize(store, node, path)) _tree.Reload();
        UpdateQueueStatus();
        _status.Text = message;
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Test hooks: queue moves and compression without the dialogs.</summary>
    internal List<DeleteJob> MoveForTest(string path, string destination, bool leaveLink) =>
        _rows?.Store.FindPath(path) is int n && n != NodeStore.None ? EnqueueJobs([n], DeleteMethod.Move, destination, leaveLink, default) : [];

    internal List<DeleteJob> CompressForTest(string path, CompressionKind kind) =>
        _rows?.Store.FindPath(path) is int n && n != NodeStore.None ? EnqueueJobs([n], DeleteMethod.Compress, null, false, kind) : [];

    internal long NodeSizeForTest(string path) => _rows?.Store.FindPath(path) is int n && n != NodeStore.None ? _rows.Store[n].Size : -1;
}
