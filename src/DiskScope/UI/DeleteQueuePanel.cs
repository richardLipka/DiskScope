using DiskScope.Core;
using DiskScope.Core.Deletion;

namespace DiskScope.UI;

/// <summary>
/// The delete queue: what waits, what is being deleted, what is done or failed. A virtual list with running totals,
/// so a queue of 100 000 items (an emptied folder) costs no more to show than ten.
/// </summary>
sealed class DeleteQueuePanel : UserControl
{
    readonly DeleteQueue _queue;
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 2) };
    readonly Button _pause = new() { AutoSize = true };
    readonly Button _cancel = new() { AutoSize = true };
    readonly Button _clear = new() { AutoSize = true };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None,
        VirtualMode = true,
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };

    readonly List<DeleteJob> _rows = [];
    readonly Dictionary<DeleteJob, int> _index = [];
    // Running totals, adjusted as jobs change state; recounting would be quadratic for big queues.
    readonly Dictionary<DeleteJob, DeleteState> _counted = [];
    int _waiting, _done, _failed;
    long _doneBytes;

    public DeleteQueuePanel(DeleteQueue queue)
    {
        _queue = queue;
        _bar.Controls.AddRange([_summary, _pause, _cancel, _clear]);
        Controls.Add(_list);
        Controls.Add(_bar);
        foreach (var w in new[] { 620, 95, 110, 360 }) _list.Columns.Add("", w);
        _list.Columns[1].TextAlign = HorizontalAlignment.Right;
        _list.RetrieveVirtualItem += (_, e) => e.Item = MakeItem(e.ItemIndex);
        _pause.Click += (_, _) =>
        {
            _queue.Paused = !_queue.Paused;
            ApplyTexts();
        };
        // The cancelled jobs come back through the queue's notifications like any other change.
        _cancel.Click += (_, _) => _queue.CancelWaiting();
        _clear.Click += (_, _) =>
        {
            _queue.ClearFinished();
            Rebuild();
        };
        ApplyTexts();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (ColumnHeader c in _list.Columns) c.Width = LogicalToDeviceUnits(c.Width);
    }

    public void ApplyTexts()
    {
        _pause.Text = L.T(_queue.Paused ? "QueueResume" : "QueuePause");
        _cancel.Text = L.T("QueueCancel");
        _clear.Text = L.T("QueueClear");
        string[] cols = ["ColLocation", "ColOnDisk", "QueueColMethod", "QueueColState"];
        for (int i = 0; i < cols.Length; i++) _list.Columns[i].Text = L.T(cols[i]);
        _list.Invalidate();
        UpdateSummary();
    }

    /// <summary>Adds newly queued jobs.</summary>
    public void Add(IEnumerable<DeleteJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (_index.ContainsKey(job)) continue;
            _index[job] = _rows.Count;
            _rows.Add(job);
            Count(job);
        }
        _list.VirtualListSize = _rows.Count;
        UpdateSummary();
    }

    /// <summary>Shows a job's new state; called on the UI thread. Call <see cref="UpdateSummary"/> after a batch.</summary>
    public void Update(DeleteJob job)
    {
        if (!_index.TryGetValue(job, out int row))
        {
            Add([job]);
            return;
        }
        Count(job);
        if (IsHandleCreated && row < _list.VirtualListSize) _list.RedrawItems(row, row, invalidateOnly: true);
    }

    void Count(DeleteJob job)
    {
        if (_counted.TryGetValue(job, out var before)) Adjust(job, before, -1);
        _counted[job] = job.State;
        Adjust(job, job.State, +1);
    }

    void Adjust(DeleteJob job, DeleteState state, int by)
    {
        switch (state)
        {
            case DeleteState.Waiting or DeleteState.Running: _waiting += by; break;
            case DeleteState.Done: _done += by; _doneBytes += by * job.Freed; break;
            case DeleteState.Failed: _failed += by; break;
        }
    }

    void Rebuild()
    {
        _rows.Clear();
        _index.Clear();
        _counted.Clear();
        _waiting = _done = _failed = 0;
        _doneBytes = 0;
        Add(_queue.Jobs);
        _list.Invalidate();
    }

    ListViewItem MakeItem(int row)
    {
        if (row >= _rows.Count) return new ListViewItem(["", "", "", ""]);
        var job = _rows[row];
        var item = new ListViewItem([
            job.Path,
            Format.Size(job.Size, L.Culture),
            MethodText(job),
            StateText(job),
        ]);
        item.ForeColor = job.State switch
        {
            DeleteState.Failed => Color.FromArgb(192, 57, 43),
            DeleteState.Done or DeleteState.Cancelled => SystemColors.GrayText,
            _ => SystemColors.WindowText,
        };
        return item;
    }

    static string MethodText(DeleteJob job) => job.Method switch
    {
        DeleteMethod.RecycleBin => L.T("QueueToBin"),
        DeleteMethod.Move => L.T("QueueMoveTo", job.Destination ?? ""),
        DeleteMethod.Compress => L.T("QueueCompress", job.Compression.ToString().ToUpperInvariant()),
        _ => L.T("QueuePermanent"),
    };

    static string Percent(DeleteJob job) => job.Size > 0 ? Math.Min(100, job.BytesDone * 100 / job.Size).ToString(L.Culture) + " %" : "";

    static string StateText(DeleteJob job) => job.State switch
    {
        DeleteState.Running when job.Method == DeleteMethod.Move => L.T("QueueStateMoving", Percent(job)),
        DeleteState.Running when job.Method == DeleteMethod.Compress => L.T("QueueStateCompressing", Percent(job)),
        DeleteState.Done when job.Method == DeleteMethod.Move => L.T("QueueStateMoved", Format.Size(job.Freed, L.Culture)),
        DeleteState.Done when job.Method == DeleteMethod.Compress && job.FailedEntries > 0 =>
            L.T("QueueStateCompressedPartly", Format.Size(job.Freed, L.Culture), job.FailedEntries, job.Error ?? ""),
        DeleteState.Done when job.Method == DeleteMethod.Compress => L.T("QueueStateCompressed", Format.Size(job.Freed, L.Culture)),
        DeleteState.Failed when job.Error == FileMover.ErrorExists => L.T("QueueStateExists"),
        DeleteState.Failed when job.Error == FileMover.ErrorIntoItself => L.T("QueueStateIntoItself"),
        DeleteState.Failed when job.Error == FileMover.ErrorContainsLinks => L.T("QueueStateContainsLinks"),
        DeleteState.Failed when job.Error == FileCompressor.ErrorNotNtfs => L.T("QueueStateNotNtfs"),
        DeleteState.Failed when job.Error == DeleteJob.ErrorCompressRefused => L.T("QueueStateCompressRefused"),
        DeleteState.Failed when job.Error == DeleteJob.ErrorProtected => L.T("QueueStateProtected"),
        DeleteState.Failed when job.Error == DeleteJob.ErrorChanged => L.T("QueueStateChanged"),
        DeleteState.Failed when job.Error == FileDeleter.ErrorUnsafeName => L.T("QueueStateUnsafeName"),
        DeleteState.Failed when job.FailedEntries > 0 => L.T("QueueStateFailedPartly", job.FailedEntries, job.Error ?? ""),
        DeleteState.Failed => L.T("QueueStateFailed", job.Error ?? ""),
        _ => L.T("QueueState" + job.State),
    };

    public void UpdateSummary() =>
        _summary.Text = L.T("QueueSummary", _waiting, _done, Format.Size(_doneBytes, L.Culture), _failed);

    /// <summary>For tests: the state text shown for each row.</summary>
    internal IEnumerable<string> RowsForTest() => _rows.Select(StateText);
}
