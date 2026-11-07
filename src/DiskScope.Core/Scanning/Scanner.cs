namespace DiskScope.Core.Scanning;

public enum ScanMode
{
    /// <summary>Pick MFT for an NTFS volume root when elevated, otherwise directory enumeration.</summary>
    Auto,
    Directory,
    Mft,
}

/// <summary>Live counters updated by scanner threads and read by the UI.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Directories;
    public long Bytes;
    public long Errors;
    /// <summary>For the MFT scanner: records processed out of <see cref="TotalRecords"/>.</summary>
    public long Records;
    public long TotalRecords;
    public volatile string? CurrentPath;
}

public interface IScanner
{
    ScanMode Mode { get; }

    /// <summary>Fills the store under its root (already created for <paramref name="rootPath"/>).</summary>
    void Scan(NodeStore store, string rootPath, ScanProgress progress, CancellationToken cancel);
}
