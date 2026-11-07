namespace DiskScope.Core;

/// <summary>
/// Extended-length paths (\\?\C:\…, \\?\UNC\server\share\…). Windows passes them to the file system unchanged:
/// no length limit, and names ending in a dot or a space keep it, whereas a normal path turns "abc." into "abc" —
/// which would make an operation hit a different file. Every file system call on a path from the tree uses this.
/// </summary>
public static class LongPath
{
    public static string Of(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    /// <summary>Whether any component ends in a dot or a space: such paths only work as extended paths.</summary>
    public static bool HasUnsafeName(string path)
    {
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part != "." && part != ".." && part != "?" && (part[^1] == '.' || part[^1] == ' ')) return true;
        return false;
    }
}
