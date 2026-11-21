using DiskScope.Core;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;

namespace DiskScope.UI;

/// <summary>Saving finished scans, opening saved ones, the data manager.</summary>
sealed partial class MainForm
{
    readonly SnapshotCatalog _catalog = new(Path.Combine(AppPaths.DataDir, "scans"));
    readonly ToolStripDropDownButton _history = new();
    SnapshotInfo? _loaded;
    /// <summary>The saved file that holds the shown tree, and the tree's version then: while they match, the tree can be reopened from it.</summary>
    string? _treeFile;
    int _treeFileVersion;
    bool _saving;
    Task? _saveTask;
    bool _loading;

    void BuildHistory()
    {
        _history.DropDownOpening += (_, _) => FillHistory();
        // An item is needed for the arrow to open the drop-down at all.
        _history.DropDownItems.Add(new ToolStripMenuItem("…"));
    }

    void ApplyHistoryTexts()
    {
        _history.Text = "🕘 " + L.T("SavedScans");
        _history.ToolTipText = L.T("SavedScansTip");
    }

    void FillHistory()
    {
        _history.DropDownItems.Clear();
        var snapshots = SelectedTarget is string target ? _catalog.ListFor(target) : [];
        foreach (var s in snapshots.Take(20))
        {
            var item = new ToolStripMenuItem(L.T("SavedScanItem", s.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture),
                Format.Count(s.Files, L.Culture), Format.Size(s.Size, L.Culture)) + (s.Complete ? "" : "  " + L.T("Incomplete")))
            {
                Checked = _loaded?.FilePath == s.FilePath,
                Enabled = !IsBusy,
            };
            item.Click += (_, _) => LoadSnapshot(s);
            _history.DropDownItems.Add(item);
        }
        if (snapshots.Count == 0) _history.DropDownItems.Add(new ToolStripMenuItem(L.T("NoSavedScans")) { Enabled = false });
        _history.DropDownItems.Add(new ToolStripSeparator());
        var manage = new ToolStripMenuItem(L.T("ManageData"));
        manage.Click += (_, _) => ShowDataManager();
        _history.DropDownItems.Add(manage);
        AddCompareItems();
    }

    internal void ShowDataManagerForTest() => ShowDataManager();

    void ShowDataManager()
    {
        using var dlg = new DataManagerForm(_catalog, _settings);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ToOpen != null && !IsBusy) LoadSnapshot(dlg.ToOpen);
        _settings.Save();
    }

    async void SaveSnapshot(ScanSession session)
    {
        _saving = true;
        UpdateButtons();
        _status.Text = L.T("Saving");
        string file = _catalog.NewFilePath(session.RootPath, session.StartedUtc);
        string? error = null;
        try
        {
            _saveTask = Task.Run(() => Snapshot.Save(session.Store, file, session.StartedUtc, session.Elapsed, session.Mode,
                session.State == ScanState.Completed, session.Progress.Errors));
            await _saveTask;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        _saving = false;
        // Closing the window waits for the save; by the time this continuation runs the window may be gone.
        if (IsDisposed) return;
        if (error == null && _session == session)
        {
            _treeFile = file;
            _treeFileVersion = session.Store.Version;
        }
        UpdateButtons();
        UpdateStatus();
        _status.Text += "  ·  " + (error == null ? L.T("Saved") : L.T("SaveFailed", error));
    }

    async void LoadSnapshot(SnapshotInfo info)
    {
        if (IsBusy) return;
        // A whole-disk tree takes gigabytes. If the shown one is saved unchanged, let it go before loading the next,
        // so that the two never need to fit in memory together; should loading fail, it is reopened from its file.
        string? restore = null;
        if (_rows != null && _treeFile != null && _treeFile != info.FilePath && _rows.Store.Version == _treeFileVersion &&
            _rows.Store.MemoryUsage > ReleaseThreshold && File.Exists(_treeFile))
        {
            restore = _treeFile;
            ReleaseTree();
        }
        _loading = true;
        UpdateButtons();
        _status.Text = L.T("Loading", 0);
        var progress = new Progress<double>(p => _status.Text = L.T("Loading", (int)(p * 100)));
        NodeStore? store = null;
        string? error = null;
        try
        {
            store = await Task.Run(() => Snapshot.Load(info.FilePath, out _, ((IProgress<double>)progress).Report));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        _loading = false;
        if (IsDisposed) return; // closed while loading
        if (store != null)
        {
            _session = null;
            _loaded = info;
            _treeFile = info.FilePath;
            _treeFileVersion = store.Version;
            SelectTarget(info.RootPath);
            ShowStore(store);
        }
        else
        {
            MessageBox.Show(this, L.T("LoadFailed", error ?? "?"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (restore != null)
            {
                SnapshotInfo? previous = null;
                try { previous = Snapshot.ReadInfo(restore); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                if (previous != null)
                {
                    LoadSnapshot(previous);
                    return;
                }
            }
        }
        UpdateButtons();
        UpdateStatus();
        SnapshotLoaded?.Invoke();
    }

    /// <summary>Trees smaller than this are kept while another loads; releasing them would only make the view flicker.</summary>
    const long ReleaseThreshold = 256L << 20;

    /// <summary>Lets go of the shown tree and everything that refers to it (the view, the panels, a comparison).</summary>
    void ReleaseTree()
    {
        _rows = null;
        _tree.Rows = null;
        _session = null;
        _loaded = null;
        _treeFile = null;
        _compare = null; // the baseline itself is kept in _compareBaseline and reattached to the next tree
        _rescanQueue.Clear();
        _search.SetStore(null);
        _cleanup.SetStore(null);
        SetAnalysisStore(null);
        UpdateBreadcrumb();
        UpdateDetail();
        GC.Collect();
    }

    internal bool TreeReleasableForTest => _rows != null && _treeFile != null && _rows.Store.Version == _treeFileVersion;

    /// <summary>Raised after a saved scan has been opened; used by the test hooks.</summary>
    public event Action? SnapshotLoaded;

    /// <summary>Opens the last scan of the selected target if the setting asks for it; false when nothing was opened.</summary>
    bool LoadLastOnStart()
    {
        if (!_settings.LoadLastOnStart || SelectedTarget is not string target) return false;
        if (_catalog.LatestFor(target) is not SnapshotInfo latest) return false;
        LoadSnapshot(latest);
        return true;
    }

    string LoadedStatusLine(SnapshotInfo info)
    {
        var store = _rows!.Store;
        ref var root = ref store[store.Root];
        int days = (DateTime.Today - info.ScanTimeUtc.ToLocalTime().Date).Days;
        string age = days == 0 ? L.T("Today") : L.T("DaysAgo", days);
        return L.T("LoadedLine", info.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture), age,
            Format.Count(root.FileCount, L.Culture), Format.Count(root.DirCount, L.Culture), Format.Size(root.Size, L.Culture));
    }
}
