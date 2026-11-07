using DiskScope.Core;
using DiskScope.Core.Storage;

namespace DiskScope.UI;

/// <summary>Comparing the shown tree with an older saved scan: the Change column and the growth filter.</summary>
sealed partial class MainForm
{
    enum CompareFilter
    {
        All,
        Grown,
        Changed,
    }

    readonly ToolStripComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false, AutoSize = false };
    TreeComparison? _compare;
    SnapshotInfo? _compareInfo;

    void BuildCompare()
    {
        _filter.Width = LogicalToDeviceUnits(170);
        _filter.SelectedIndexChanged += (_, _) => ApplyCompareToRows();
    }

    void ApplyCompareTexts()
    {
        int selected = Math.Max(0, _filter.SelectedIndex);
        _filter.Items.Clear();
        _filter.Items.AddRange([L.T("ShowAll"), L.T("ShowGrown"), L.T("ShowChanged")]);
        _filter.SelectedIndex = selected;
        _filter.ToolTipText = L.T("FilterTip");
    }

    void AddCompareItems()
    {
        if (_rows == null) return;
        string root = _rows.Store.RootPath.TrimEnd('\\');
        // Older scans of this folder, or of a drive or folder that contains it.
        var candidates = _catalog.List().Where(s =>
        {
            string r = s.RootPath.TrimEnd('\\');
            return s.FilePath != _loaded?.FilePath &&
                   (root.Equals(r, StringComparison.OrdinalIgnoreCase) || root.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        }).Take(20).ToList();

        _history.DropDownItems.Add(new ToolStripSeparator());
        var compare = new ToolStripMenuItem(L.T("CompareWith")) { Enabled = candidates.Count > 0 && !IsScanning && !_loading };
        foreach (var s in candidates)
        {
            var item = new ToolStripMenuItem(L.T("SavedScanItem", s.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture),
                Format.Count(s.Files, L.Culture), Format.Size(s.Size, L.Culture)) + "   " + s.RootPath)
            { Checked = _compareInfo?.FilePath == s.FilePath };
            item.Click += (_, _) => StartCompare(s);
            compare.DropDownItems.Add(item);
        }
        _history.DropDownItems.Add(compare);
        if (_compare != null)
        {
            var stop = new ToolStripMenuItem(L.T("StopCompare"));
            stop.Click += (_, _) => StopCompare();
            _history.DropDownItems.Add(stop);
        }
    }

    async void StartCompare(SnapshotInfo info)
    {
        // Saving only reads the tree, so a comparison may start while a finished scan is being written.
        if (_rows == null || IsScanning || _loading || _rescanning) return;
        _loading = true;
        UpdateButtons();
        var progress = new Progress<double>(p => _status.Text = L.T("Loading", (int)(p * 100)));
        try
        {
            var baseline = await Task.Run(() => Snapshot.Load(info.FilePath, out _, ((IProgress<double>)progress).Report));
            _compareInfo = info;
            _compare = new TreeComparison(_rows.Store, baseline);
            if (_tree.Columns.Count <= (int)Col.Delta)
                _tree.Columns.Add(L.T("ColDelta"), LogicalToDeviceUnits(120), HorizontalAlignment.Right);
            _rows.Sort = SortColumn.Delta;
            _rows.Descending = true;
            _filter.Visible = true;
            ApplyCompareToRows();
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
            UpdateDetail();
            CompareStarted?.Invoke();
        }
    }

    /// <summary>Test hook: compares with the newest saved scan of this root taken before the current one.</summary>
    internal void CompareWithPreviousForTest(bool onlyGrown)
    {
        if (_rows == null) return;
        var started = _session?.StartedUtc ?? _loaded?.ScanTimeUtc ?? DateTime.UtcNow;
        var previous = _catalog.ListFor(_rows.Store.RootPath).FirstOrDefault(s => s.ScanTimeUtc < started.AddSeconds(-1));
        if (previous == null) return;
        if (onlyGrown) _filter.SelectedIndex = (int)CompareFilter.Grown;
        StartCompare(previous);
    }

    /// <summary>Raised when a comparison has been set up; used by the test hooks.</summary>
    public event Action? CompareStarted;

    void StopCompare()
    {
        _compare = null;
        _compareInfo = null;
        _filter.Visible = false;
        if (_tree.Columns.Count > (int)Col.Delta) _tree.Columns.RemoveAt((int)Col.Delta);
        if (_rows != null)
        {
            if (_rows.Sort == SortColumn.Delta) _rows.Sort = SortColumn.Size;
            _rows.Descending = true;
        }
        ApplyCompareToRows();
        UpdateStatus();
        UpdateDetail();
    }

    /// <summary>After a new tree is shown, compares it with the same baseline as before.</summary>
    void ReattachCompare()
    {
        if (_compare == null || _rows == null) return;
        _compare = new TreeComparison(_rows.Store, _compare.Baseline);
        _rows.Sort = SortColumn.Delta;
        ApplyCompareToRows();
    }

    void ApplyCompareToRows()
    {
        if (_rows == null) return;
        var cmp = _compare;
        _rows.DeltaOf = cmp == null ? null : cmp.Delta;
        _tree.DeltaOf = cmp == null ? null : n => cmp.Delta(n);
        _rows.Filter = cmp == null ? null : (CompareFilter)Math.Max(0, _filter.SelectedIndex) switch
        {
            CompareFilter.Grown => n => cmp.Delta(n) > 0,
            CompareFilter.Changed => n => cmp.Delta(n) != 0,
            _ => null,
        };
        _tree.UpdateSortIcon();
        _tree.Reload();
    }

    string CompareStatus()
    {
        if (_compare == null || _compareInfo == null || _rows == null) return "";
        return "  ·  " + L.T("ComparedWith", _compareInfo.ScanTimeUtc.ToLocalTime().ToString("g", L.Culture),
            Format.SignedSize(_compare.Delta(_rows.Store.Root), L.Culture));
    }

    string CompareDetail(int node)
    {
        if (_compare == null || _rows == null) return "";
        string text = "  ·  " + (_compare.IsNew(node) ? L.T("NewItem") : L.T("ChangeBy", Format.SignedSize(_compare.Delta(node), L.Culture)));
        if (_rows.Store[node].IsDirectory && !_compare.IsNew(node))
        {
            var removed = _compare.Removed(node);
            if (removed.Count > 0)
                text += "  ·  " + L.T("RemovedSince", removed.Count, Format.Size(removed.Sum(r => r.Size), L.Culture));
        }
        return text;
    }
}
