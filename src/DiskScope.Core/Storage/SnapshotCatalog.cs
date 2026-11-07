using System.Globalization;

namespace DiskScope.Core.Storage;

/// <summary>The folder of saved snapshots.</summary>
public sealed class SnapshotCatalog(string directory)
{
    public string Directory { get; } = directory;

    public string NewFilePath(string rootPath, DateTime scanTimeUtc)
    {
        var safe = new string(rootPath.TrimEnd('\\').Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (safe.Length > 80) safe = safe[..80];
        string stamp = scanTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Directory, $"{safe}_{stamp}{Snapshot.Extension}");
    }

    /// <summary>All readable snapshots, newest first. Unreadable files are skipped.</summary>
    public List<SnapshotInfo> List()
    {
        var list = new List<SnapshotInfo>();
        if (!System.IO.Directory.Exists(Directory)) return list;
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + Snapshot.Extension))
        {
            try { list.Add(Snapshot.ReadInfo(file)); } catch { }
        }
        return list.OrderByDescending(s => s.ScanTimeUtc).ToList();
    }

    public List<SnapshotInfo> ListFor(string rootPath) =>
        List().Where(s => string.Equals(s.RootPath.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();

    public SnapshotInfo? LatestFor(string rootPath) => ListFor(rootPath).FirstOrDefault();

    /// <summary>Bytes taken by everything in the folder, including leftovers that are not valid snapshots.</summary>
    public long TotalBytes() => System.IO.Directory.Exists(Directory)
        ? System.IO.Directory.EnumerateFiles(Directory).Sum(f => new FileInfo(f).Length) : 0;

    public void Delete(SnapshotInfo snapshot) => File.Delete(snapshot.FilePath);

    /// <summary>Deletes all snapshot files (and interrupted temporary files) from the folder.</summary>
    public void DeleteAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            if (file.EndsWith(Snapshot.Extension, StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(Snapshot.Extension + ".tmp", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
    }
}
