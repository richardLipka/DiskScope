using DiskScope.Core.Native;

namespace DiskScope.Core.Deletion;

/// <summary>Outcome of moving one item.</summary>
public readonly record struct MoveResult(bool Moved, long Freed, int FailedEntries, string? Error);

/// <summary>
/// Moves a file or folder into another folder, to free space on its drive without deleting it.
/// On the same drive it is a rename. To another drive everything is copied first (attributes and times included),
/// the copy is checked, and only then is the original deleted; if anything fails before that point, the partial
/// copy is removed and the original stays untouched. A folder can leave a junction behind, so that programs looking
/// for it at the old place still find it.
/// </summary>
public static class FileMover
{
    /// <summary><see cref="MoveResult.Error"/> values the UI translates.</summary>
    public const string ErrorExists = "#exists";
    public const string ErrorIntoItself = "#into-itself";
    public const string ErrorContainsLinks = "#contains-links";

    public static MoveResult Move(string source, bool isDirectory, string destinationFolder, bool leaveLink, Action<long>? progress,
        CancellationToken cancel) => Move(source, isDirectory, destinationFolder, leaveLink, progress, cancel, forceCopy: false);

    /// <param name="forceCopy">Copy and delete even on the same drive (for tests, which have only one drive).</param>
    internal static MoveResult Move(string source, bool isDirectory, string destinationFolder, bool leaveLink, Action<long>? progress,
        CancellationToken cancel, bool forceCopy)
    {
        string src = Path.TrimEndingDirectorySeparator(source);
        string name = Path.GetFileName(src);
        string dest = Path.Combine(Path.TrimEndingDirectorySeparator(destinationFolder), name);
        string srcFull = LongPath.Of(src), destFull = LongPath.Of(dest);
        bool dirExists = Directory.Exists(srcFull);
        isDirectory |= dirExists;
        if (!dirExists && !File.Exists(srcFull)) return new MoveResult(false, 0, 0, new FileNotFoundException(null, src).Message);
        if (File.Exists(destFull) || Directory.Exists(destFull)) return new MoveResult(false, 0, 0, ErrorExists);
        if (isDirectory && (dest + "\\").StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) return new MoveResult(false, 0, 0, ErrorIntoItself);
        Directory.CreateDirectory(LongPath.Of(destinationFolder));

        bool sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);
        long size = isDirectory ? 0 : new FileInfo(srcFull).Length;
        if (sameVolume && !forceCopy)
        {
            if (isDirectory) Directory.Move(srcFull, destFull);
            else File.Move(srcFull, destFull);
            progress?.Invoke(size);
            if (leaveLink && isDirectory) Junction.Create(src, dest);
            return new MoveResult(true, 0, 0, null);
        }

        // To another drive: copy everything, check it, then delete the original.
        long copied = 0;
        try
        {
            if (isDirectory)
            {
                if (!CopyTree(srcFull, destFull, ref copied, progress, cancel, out string? linkProblem))
                    throw new IOException(linkProblem);
            }
            else
            {
                File.Copy(srcFull, destFull);
                copied = size;
                progress?.Invoke(copied);
            }
            Verify(srcFull, destFull, isDirectory);
        }
        catch (Exception ex)
        {
            // Nothing has been lost yet: remove the partial copy and keep the original.
            int ignored = 0;
            string? ignoredError = null;
            FileDeleter.DeleteTreeAt(destFull, ref ignored, ref ignoredError, CancellationToken.None);
            if (ex is OperationCanceledException) throw;
            return new MoveResult(false, 0, 0, ex.Message == ErrorContainsLinks ? ErrorContainsLinks : ex.Message);
        }

        int failed = 0;
        string? error = null;
        FileDeleter.DeleteTreeAt(srcFull, ref failed, ref error, CancellationToken.None);
        bool gone = !Directory.Exists(srcFull) && !File.Exists(srcFull);
        if (gone && leaveLink && isDirectory) Junction.Create(src, dest);
        // When some originals could not be deleted, the data now exists twice; nothing is lost.
        return new MoveResult(gone, gone ? copied : copied - Measure(srcFull), failed, gone ? null : error);
    }

    /// <summary>
    /// Copies a folder tree iteratively. Links inside are refused before anything is copied: copying would either
    /// duplicate what they lead to or break them.
    /// </summary>
    static bool CopyTree(string src, string dest, ref long copied, Action<long>? progress, CancellationToken cancel, out string? problem)
    {
        problem = null;
        var dirs = new List<(string Src, string Dest)>();
        var files = new List<(string Src, string Dest)>();
        var stack = new Stack<(string Src, string Dest)>();
        stack.Push((src, dest));
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (s, d) = stack.Pop();
            dirs.Add((s, d));
            foreach (var entry in new DirectoryInfo(s).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    problem = ErrorContainsLinks;
                    return false;
                }
                string target = Path.Combine(d, entry.Name);
                if (entry is DirectoryInfo) stack.Push((entry.FullName, target));
                else files.Add((entry.FullName, target));
            }
        }
        foreach (var (_, d) in dirs) Directory.CreateDirectory(d);
        foreach (var (s, d) in files)
        {
            cancel.ThrowIfCancellationRequested();
            File.Copy(s, d); // keeps attributes and the last write time
            copied += new FileInfo(d).Length;
            progress?.Invoke(copied);
        }
        // Folder times last: creating entries inside a folder changes its time.
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var info = new DirectoryInfo(dirs[i].Src);
            Directory.SetLastWriteTimeUtc(dirs[i].Dest, info.LastWriteTimeUtc);
            Directory.SetCreationTimeUtc(dirs[i].Dest, info.CreationTimeUtc);
            File.SetAttributes(dirs[i].Dest, info.Attributes);
        }
        return true;
    }

    /// <summary>The copy must have the same files with the same sizes; otherwise the original is not deleted.</summary>
    static void Verify(string src, string dest, bool isDirectory)
    {
        if (!isDirectory)
        {
            if (new FileInfo(src).Length != new FileInfo(dest).Length) throw new IOException("The copy differs from the original.");
            return;
        }
        var a = Listing(src);
        var b = Listing(dest);
        if (a.Count != b.Count || a.Any(x => !b.TryGetValue(x.Key, out long len) || len != x.Value))
            throw new IOException("The copy differs from the original.");
    }

    static Dictionary<string, long> Listing(string root)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            map[Path.GetRelativePath(root, f.FullName)] = f.Length;
        return map;
    }

    static long Measure(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
