namespace DiskScope.Core.Scanning;

/// <summary>Reads the NTFS Master File Table directly. Requires elevation.</summary>
public sealed class MftScanner : IScanner
{
    public ScanMode Mode => ScanMode.Mft;

    public static bool CanScan(string root) => false;

    public void Scan(NodeStore store, string rootPath, ScanProgress progress, CancellationToken cancel) =>
        throw new NotSupportedException();
}
