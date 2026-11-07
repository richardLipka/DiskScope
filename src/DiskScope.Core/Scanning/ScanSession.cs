using System.Diagnostics;
using DiskScope.Core.Native;

namespace DiskScope.Core.Scanning;

public enum ScanState
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>One scan of a drive or folder running on a background thread.</summary>
public sealed class ScanSession
{
    readonly CancellationTokenSource _cancel = new();
    readonly Stopwatch _clock = new();
    IScanner _scanner;
    Thread? _thread;

    public NodeStore Store { get; } = new();
    public ScanProgress Progress { get; } = new();
    public string RootPath { get; }
    public ScanMode Mode => _scanner.Mode;
    public ScanState State { get; private set; } = ScanState.Running;
    public Exception? Error { get; private set; }

    /// <summary>Why the MFT scan was abandoned for directory enumeration, if it was.</summary>
    public Exception? MftError { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Raised on the scanner thread when the scan ends for any reason.</summary>
    public event Action<ScanSession>? Finished;

    public ScanSession(string rootPath, ScanMode mode = ScanMode.Auto)
    {
        RootPath = NormalizeRoot(rootPath);
        _scanner = CreateScanner(RootPath, mode);
    }

    public static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length == 3 && full[1] == ':' ? full : full.TrimEnd('\\');
    }

    static IScanner CreateScanner(string root, ScanMode mode)
    {
        if (mode == ScanMode.Auto) mode = MftScanner.CanScan(root) ? ScanMode.Mft : ScanMode.Directory;
        return mode == ScanMode.Mft ? new MftScanner() : new DirectoryScanner();
    }

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("Already started.");
        if (Elevation.IsElevated) NativeMethods.EnablePrivilege("SeBackupPrivilege");
        long mtime = 0;
        try { mtime = Directory.GetLastWriteTimeUtc(RootPath).ToFileTimeUtc(); } catch { }
        Store.CreateRoot(RootPath, mtime);
        _clock.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "ScanSession" };
        _thread.Start();
    }

    public void Cancel() => _cancel.Cancel();

    /// <summary>Blocks until the scan finishes; for tests and command-line use.</summary>
    public void Wait() => _thread?.Join();

    void Run()
    {
        try
        {
            try
            {
                _scanner.Scan(Store, RootPath, Progress, _cancel.Token);
            }
            catch (Exception ex) when (_scanner is MftScanner && ex is not OperationCanceledException)
            {
                // Reading the MFT can fail on unusual volumes; enumerating directories always works.
                MftError = ex;
                Store.ResetToRoot();
                Progress.TotalRecords = 0;
                Progress.Files = Progress.Directories = Progress.Bytes = 0;
                _scanner = new DirectoryScanner();
                _scanner.Scan(Store, RootPath, Progress, _cancel.Token);
            }
            State = ScanState.Completed;
        }
        catch (OperationCanceledException)
        {
            State = ScanState.Cancelled;
        }
        catch (Exception ex)
        {
            Error = ex;
            State = ScanState.Failed;
        }
        finally
        {
            _clock.Stop();
            Progress.CurrentPath = null;
            Store.MarkChanged();
        }
        Finished?.Invoke(this);
    }
}
