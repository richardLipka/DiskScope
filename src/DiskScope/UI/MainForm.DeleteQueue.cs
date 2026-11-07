using DiskScope.Core;
using DiskScope.Core.Deletion;

namespace DiskScope.UI;

/// <summary>Deleting through a queue processed one item at a time in the background, with its own panel.</summary>
sealed partial class MainForm
{
    readonly FileDeleter _deleter = new();
    readonly DeleteQueue _queue;
    readonly DeleteQueuePanel _queuePanel;
    readonly TabPage _deleteTab = new();
    readonly ToolStripStatusLabel _queueStatus = new() { IsLink = true, Visible = false };
    /// <summary>Finished jobs whose tree updates wait for the running scan to end.</summary>
    readonly List<DeleteJob> _deferredJobs = [];
    /// <summary>
    /// Job changes reported by the worker, applied to the view in batches by a timer: emptying a folder of 100 000
    /// files must not rebuild the tree and the panels 100 000 times.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentQueue<DeleteJob> _jobUpdates = new();
    readonly System.Windows.Forms.Timer _queueTimer = new() { Interval = 200 };
    long _freedThisSession;

    void BuildDeleteQueue()
    {
        _queue.JobChanged += _jobUpdates.Enqueue;
        _queueTimer.Tick += (_, _) => DrainJobUpdates();
        _queueTimer.Start();
        _queuePanel.Dock = DockStyle.Fill;
        _deleteTab.Controls.Add(_queuePanel);
        _queueStatus.Click += (_, _) => ShowBottom(_deleteTab);
        HandleCreated += (_, _) => _deleter.OwnerWindow = Handle;
    }

    void ApplyQueueTexts()
    {
        _deleteTab.Text = L.T("QueueTab");
        _queuePanel.ApplyTexts();
        UpdateQueueStatus();
    }

    /// <summary>Marks the items in the tree and hands them to the queue.</summary>
    List<DeleteJob> EnqueueDelete(List<int> nodes, bool permanent)
    {
        var store = _rows!.Store;
        var method = permanent ? DeleteMethod.Permanent : DeleteMethod.RecycleBin;
        var jobs = nodes.Select(n =>
        {
            ref var e = ref store[n];
            // Files are checked against their scanned size and time before deletion (hard-link entries carry no size).
            bool check = !e.IsDirectory && (e.Flags & NodeFlags.HardLink) == 0;
            return new DeleteJob(store.GetPath(n), e.IsDirectory, e.Size, method)
            {
                Store = store, Node = n,
                ExpectedLength = check ? e.Length : null,
                ExpectedMTime = check ? e.MTime : null,
            };
        }).ToList();
        foreach (int n in nodes) store.SetFlag(n, NodeFlags.Queued);
        _queue.Enqueue(jobs);
        _queuePanel.Add(jobs);
        _tree.Invalidate();
        UpdateQueueStatus();
        return jobs;
    }

    /// <summary>Applies the job changes reported since the last tick, all at once.</summary>
    void DrainJobUpdates()
    {
        if (_jobUpdates.IsEmpty || IsDisposed) return;
        var finished = new List<DeleteJob>();
        var seen = new HashSet<DeleteJob>();
        while (_jobUpdates.TryDequeue(out var job))
            if (seen.Add(job))
            {
                _queuePanel.Update(job);
                if (job.Finished) finished.Add(job);
            }
        _queuePanel.UpdateSummary();
        ApplyFinishedJobs(finished);
        UpdateQueueStatus();
    }

    /// <summary>
    /// Brings the tree in line with finished jobs: removes what was deleted (in one pass), rescans folders that were
    /// only partly deleted, then refreshes the view once.
    /// </summary>
    void ApplyFinishedJobs(List<DeleteJob> jobs)
    {
        var removed = new List<int>();
        var rescan = new List<int>();
        foreach (var job in jobs)
        {
            var store = job.Store;
            if (store == null || job.Node == NodeStore.None || !store.IsLive(job.Node))
            {
                job.Store = null;
                continue;
            }
            if (store != _rows?.Store)
            {
                store.ClearFlag(job.Node, NodeFlags.Queued);
                job.Store = null;
                continue;
            }
            if ((IsScanning || _rescanning) && job.State != DeleteState.Cancelled)
            {
                // The scanner (or a folder rescan, which rebuilds part of the tree in the background) may still be
                // changing this part of the tree; removing nodes at the same time would break its totals. Apply once
                // it is done.
                if (!_deferredJobs.Contains(job)) _deferredJobs.Add(job);
                continue;
            }
            store.ClearFlag(job.Node, NodeFlags.Queued);
            job.Store = null; // applied; the node number below is all that is still needed
            if (job.State == DeleteState.Done)
            {
                removed.Add(job.Node);
                _freedThisSession += job.Size;
            }
            // Partly deleted: rescan so its size is right. A folder on a disk that went away is left as it was,
            // since rescanning would only drop its contents from the tree.
            else if (job.State == DeleteState.Failed && job.IsDirectory && Directory.Exists(LongPath.Of(job.Path)))
                rescan.Add(job.Node);
        }
        if (_rows == null || removed.Count + rescan.Count == 0 && jobs.Count == 0) return;

        if (removed.Count > 0)
        {
            int focused = _tree.FocusedNode;
            int row = focused == NodeStore.None ? -1 : _rows.IndexOf(focused);
            bool focusGone = focused != NodeStore.None && removed.Contains(focused);
            _rows.Store.RemoveMany(removed);
            _tree.Reload();
            if (focusGone && row >= 0) _tree.SelectRow(Math.Min(row, _rows.Count - 1));
        }
        foreach (int dir in rescan) Rescan(dir);
        _tree.Invalidate();
        UpdateDetail();
        RefreshSearchAfterChange();
    }

    void ApplyDeferredJobs()
    {
        var jobs = _deferredJobs.ToList();
        _deferredJobs.Clear();
        ApplyFinishedJobs(jobs);
    }

    void UpdateQueueStatus()
    {
        int waiting = _queue.WaitingCount;
        _queueStatus.Visible = waiting > 0 || _freedThisSession > 0;
        _queueStatus.Text = waiting > 0
            ? L.T("QueueStatusBusy", waiting, Format.Size(_freedThisSession, L.Culture))
            : L.T("QueueStatusIdle", Format.Size(_freedThisSession, L.Culture));
    }

    internal void QueueAllPausedForTest()
    {
        if (_rows == null) return;
        _queue.Paused = true;
        _queuePanel.ApplyTexts();
        var children = new List<int>();
        for (int c = _rows.Store[_rows.Store.Root].FirstChild; c != NodeStore.None; c = _rows.Store[c].NextSibling) children.Add(c);
        EnqueueDelete(children.Take(children.Count / 2).ToList(), permanent: false);
        EnqueueDelete(children.Skip(children.Count / 2).ToList(), permanent: true);
        ShowBottom(_deleteTab);
    }

    internal IEnumerable<string> QueueReportForTest() =>
        _queue.Jobs.Select(j => $"job {Path.GetFileName(j.Path)} {j.Method} {j.State} {j.Error}");

    internal void CancelQueueForTest()
    {
        _queue.CancelWaiting();
        DrainJobUpdates();
    }

    /// <summary>Test hook: queues the contents of a folder for permanent deletion, as "Empty permanently" does.</summary>
    internal int EmptyFolderForTest(string path)
    {
        if (_rows?.Store.FindPath(path) is not int folder || folder == NodeStore.None) return 0;
        var store = _rows.Store;
        var children = new List<int>();
        for (int c = store[folder].FirstChild; c != NodeStore.None; c = store[c].NextSibling) children.Add(c);
        EnqueueDelete(children, permanent: true);
        ShowBottom(_deleteTab);
        return children.Count;
    }

    internal bool QueueIdleForTest => _queue.WaitingCount == 0 && _jobUpdates.IsEmpty;

    internal string QueueSummaryForTest => $"waiting {_queue.WaitingCount}, freed {_freedThisSession}";

    /// <summary>Asks before closing while items still wait; true when the window may close.</summary>
    bool ConfirmCloseWithQueue()
    {
        int waiting = _queue.WaitingCount;
        return waiting == 0 ||
               MessageBox.Show(this, L.T("QueueConfirmClose", waiting), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                   MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
