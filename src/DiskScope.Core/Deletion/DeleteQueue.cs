namespace DiskScope.Core.Deletion;

public enum DeleteState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One item waiting in or processed by the delete queue.</summary>
public sealed class DeleteJob(string path, bool isDirectory, long size, DeleteMethod method)
{
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public long Size { get; } = size;
    public DeleteMethod Method { get; } = method;
    public DeleteState State { get; internal set; } = DeleteState.Waiting;
    public string? Error { get; internal set; }
    public int FailedEntries { get; internal set; }

    /// <summary>The tree and node the item came from, so the view can be updated when it is done.</summary>
    public NodeStore? Store { get; init; }
    public int Node { get; init; } = NodeStore.None;

    public bool Finished => State is DeleteState.Done or DeleteState.Failed or DeleteState.Cancelled;
}

/// <summary>
/// Deletes queued items one after another on a background thread, so the user can keep working and keep adding
/// items. Progress is reported per item through <see cref="JobChanged"/> (raised on the worker thread).
/// </summary>
public sealed class DeleteQueue : IDisposable
{
    readonly IFileDeleter _deleter;
    readonly List<DeleteJob> _jobs = [];
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _shutdown = new();
    readonly Thread _worker;
    volatile bool _paused;

    public DeleteQueue(IFileDeleter deleter)
    {
        _deleter = deleter;
        _worker = new Thread(Run) { IsBackground = true, Name = "DeleteQueue" };
        // The shell's recycle-bin operation wants a single-threaded apartment.
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public event Action<DeleteJob>? JobChanged;

    public List<DeleteJob> Jobs
    {
        get { lock (_lock) return [.. _jobs]; }
    }

    public int WaitingCount
    {
        get { lock (_lock) return _jobs.Count(j => j.State is DeleteState.Waiting or DeleteState.Running); }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) _signal.Release();
        }
    }

    public void Enqueue(IEnumerable<DeleteJob> jobs)
    {
        lock (_lock) _jobs.AddRange(jobs);
        _signal.Release();
    }

    /// <summary>Takes back everything not started yet.</summary>
    public List<DeleteJob> CancelWaiting()
    {
        List<DeleteJob> cancelled;
        lock (_lock)
        {
            cancelled = _jobs.Where(j => j.State == DeleteState.Waiting).ToList();
            foreach (var j in cancelled) j.State = DeleteState.Cancelled;
        }
        foreach (var j in cancelled) JobChanged?.Invoke(j);
        return cancelled;
    }

    public void ClearFinished()
    {
        lock (_lock) _jobs.RemoveAll(j => j.Finished);
    }

    /// <summary>Blocks until nothing is waiting (for tests and shutdown).</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (WaitingCount > 0)
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    void Run()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            DeleteJob? job = null;
            if (!_paused)
                lock (_lock)
                {
                    job = _jobs.FirstOrDefault(j => j.State == DeleteState.Waiting);
                    if (job != null) job.State = DeleteState.Running;
                }
            if (job == null)
            {
                try { _signal.Wait(token); } catch (OperationCanceledException) { return; }
                continue;
            }

            JobChanged?.Invoke(job);
            try
            {
                var result = _deleter.Delete(job.Path, job.IsDirectory, job.Method, token);
                job.FailedEntries = result.FailedEntries;
                job.Error = result.Error;
                job.State = result.Gone ? DeleteState.Done : DeleteState.Failed;
            }
            catch (OperationCanceledException)
            {
                job.State = DeleteState.Cancelled;
                return;
            }
            catch (Exception ex)
            {
                job.Error = ex.Message;
                job.State = DeleteState.Failed;
            }
            JobChanged?.Invoke(job);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _worker.Join(TimeSpan.FromSeconds(5));
    }
}
