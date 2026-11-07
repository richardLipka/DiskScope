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
        try
        {
            _saveTask = Task.Run(() => Snapshot.Save(session.Store, file, session.StartedUtc, session.Elapsed, session.Mode,
                session.State == ScanState.Completed, session.Progress.Errors));
            await _saveTask;
            UpdateStatus();
            _status.Text += "  ·  " + L.T("Saved");
        }
        catch (Exception ex)
        {
            UpdateStatus();
            _status.Text += "  ·  " + L.T("SaveFailed", ex.Message);
        }
        finally
        {
            _saving = false;
            UpdateButtons();
        }
    }

    async void LoadSnapshot(SnapshotInfo info)
    {
        if (IsBusy) return;
        _loading = true;
        UpdateButtons();
        _status.Text = L.T("Loading", 0);
        var progress = new Progress<double>(p => _status.Text = L.T("Loading", (int)(p * 100)));
        try
        {
            var store = await Task.Run(() => Snapshot.Load(info.FilePath, out _, ((IProgress<double>)progress).Report));
            _session = null;
            _loaded = info;
            SelectTarget(info.RootPath);
            ShowStore(store);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("LoadFailed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _loading = false;
            UpdateButtons();
            UpdateStatus();
            SnapshotLoaded?.Invoke();
        }
    }

    /// <summary>Raised after a saved scan has been opened; used by the test hooks.</summary>
    public event Action? SnapshotLoaded;

    void LoadLastOnStart()
    {
        if (!_settings.LoadLastOnStart || SelectedTarget is not string target) return;
        if (_catalog.LatestFor(target) is SnapshotInfo latest) LoadSnapshot(latest);
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
