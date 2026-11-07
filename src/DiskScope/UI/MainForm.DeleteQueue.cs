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
    long _freedThisSession;

    void BuildDeleteQueue()
    {
        _queue.JobChanged += job => PostToUi(() => OnJobChanged(job));
        _queuePanel.Dock = DockStyle.Fill;
        _queuePanel.Cancelled += jobs =>
        {
            foreach (var job in jobs) OnJobChanged(job);
        };
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
        foreach (var job in jobs) _queuePanel.Update(job);
        _tree.Invalidate();
        UpdateQueueStatus();
        return jobs;
    }

    void OnJobChanged(DeleteJob job)
    {
        _queuePanel.Update(job);
        if (job.Finished) ApplyFinishedJob(job);
        UpdateQueueStatus();
    }

    /// <summary>Brings the tree in line with a finished job: removes what was deleted, rescans what was only partly deleted.</summary>
    void ApplyFinishedJob(DeleteJob job)
    {
        var store = job.Store;
        if (store == null || job.Node == NodeStore.None || !store.IsLive(job.Node)) return;
        if (store != _rows?.Store)
        {
            store.ClearFlag(job.Node, NodeFlags.Queued);
            return;
        }
        if (IsScanning && job.State != DeleteState.Cancelled)
        {
            // The scanner may still be adding to this part of the tree; apply once it is done.
            if (!_deferredJobs.Contains(job)) _deferredJobs.Add(job);
            return;
        }
        store.ClearFlag(job.Node, NodeFlags.Queued);
        if (job.State == DeleteState.Done)
        {
            int row = _rows.IndexOf(job.Node);
            bool wasFocused = _tree.FocusedNode == job.Node;
            store.Remove(job.Node);
            _freedThisSession += job.Size;
            _tree.Reload();
            if (wasFocused && row >= 0) _tree.SelectRow(Math.Min(row, _rows.Count - 1));
        }
        else if (job.State == DeleteState.Failed && job.IsDirectory)
        {
            Rescan(job.Node);
        }
        _tree.Invalidate();
        UpdateDetail();
        RefreshSearchAfterChange();
    }

    void ApplyDeferredJobs()
    {
        var jobs = _deferredJobs.ToList();
        _deferredJobs.Clear();
        foreach (var job in jobs) ApplyFinishedJob(job);
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

    internal void CancelQueueForTest()
    {
        foreach (var job in _queue.CancelWaiting()) OnJobChanged(job);
    }

    /// <summary>Asks before closing while items still wait; true when the window may close.</summary>
    bool ConfirmCloseWithQueue()
    {
        int waiting = _queue.WaitingCount;
        return waiting == 0 ||
               MessageBox.Show(this, L.T("QueueConfirmClose", waiting), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                   MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
