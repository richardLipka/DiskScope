using DiskScope.Core;
using DiskScope.Core.Deletion;

namespace DiskScope.UI;

/// <summary>The delete queue: what waits, what is being deleted, what is done or failed.</summary>
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
    };
    readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };
    readonly Dictionary<DeleteJob, ListViewItem> _items = [];

    /// <summary>Cancel pressed: the jobs taken back, so the view can unmark them.</summary>
    public event Action<List<DeleteJob>>? Cancelled;

    public DeleteQueuePanel(DeleteQueue queue)
    {
        _queue = queue;
        _bar.Controls.AddRange([_summary, _pause, _cancel, _clear]);
        Controls.Add(_list);
        Controls.Add(_bar);
        foreach (var w in new[] { 620, 95, 110, 360 }) _list.Columns.Add("", w);
        _list.Columns[1].TextAlign = HorizontalAlignment.Right;
        _pause.Click += (_, _) =>
        {
            _queue.Paused = !_queue.Paused;
            ApplyTexts();
        };
        _cancel.Click += (_, _) => Cancelled?.Invoke(_queue.CancelWaiting());
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
        Rebuild();
    }

    /// <summary>Shows a job's new state; called on the UI thread.</summary>
    public void Update(DeleteJob job)
    {
        if (!_items.TryGetValue(job, out var item))
        {
            item = new ListViewItem(["", "", "", ""]) { Tag = job };
            _items[job] = item;
            _list.Items.Add(item);
        }
        Fill(item, job);
        UpdateSummary();
    }

    void Rebuild()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _items.Clear();
        foreach (var job in _queue.Jobs)
        {
            var item = new ListViewItem(["", "", "", ""]) { Tag = job };
            Fill(item, job);
            _items[job] = item;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateSummary();
    }

    static void Fill(ListViewItem item, DeleteJob job)
    {
        item.SubItems[0].Text = job.Path;
        item.SubItems[1].Text = Format.Size(job.Size, L.Culture);
        item.SubItems[2].Text = L.T(job.Method == DeleteMethod.RecycleBin ? "QueueToBin" : "QueuePermanent");
        item.SubItems[3].Text = job.State switch
        {
            DeleteState.Failed when job.Error == DeleteJob.ErrorProtected => L.T("QueueStateProtected"),
            DeleteState.Failed when job.Error == DeleteJob.ErrorChanged => L.T("QueueStateChanged"),
            DeleteState.Failed when job.FailedEntries > 0 => L.T("QueueStateFailedPartly", job.FailedEntries, job.Error ?? ""),
            DeleteState.Failed => L.T("QueueStateFailed", job.Error ?? ""),
            _ => L.T("QueueState" + job.State),
        };
        item.ForeColor = job.State switch
        {
            DeleteState.Failed => Color.FromArgb(192, 57, 43),
            DeleteState.Done or DeleteState.Cancelled => SystemColors.GrayText,
            _ => SystemColors.WindowText,
        };
    }

    void UpdateSummary()
    {
        var jobs = _queue.Jobs;
        _summary.Text = L.T("QueueSummary",
            jobs.Count(j => j.State is DeleteState.Waiting or DeleteState.Running),
            jobs.Count(j => j.State == DeleteState.Done),
            Format.Size(jobs.Where(j => j.State == DeleteState.Done).Sum(j => j.Size), L.Culture),
            jobs.Count(j => j.State == DeleteState.Failed));
    }
}
