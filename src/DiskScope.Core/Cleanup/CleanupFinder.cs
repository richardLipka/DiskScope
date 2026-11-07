using System.IO.Enumeration;

namespace DiskScope.Core.Cleanup;

public sealed record CleanupMatch(CatalogEntry Entry, int Node, long Size);

/// <summary>Finds the catalog's locations in a scanned tree (only in the tree: the disk is not touched).</summary>
public static class CleanupFinder
{
    /// <summary>Folder-name entries (node_modules…) report at most this many of their largest matches.</summary>
    public const int MaxMatchesPerEntry = 300;

    public static List<CleanupMatch> Find(NodeStore store, CleanupCatalog catalog, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var matches = new List<CleanupMatch>();
        var taken = new HashSet<int>();
        foreach (var entry in catalog.Entries)
        {
            var nodes = new List<int>();
            foreach (string pattern in entry.Paths) nodes.AddRange(ResolvePath(store, ExpandPattern(pattern, expand)));
            if (entry.FolderNames.Count > 0) nodes.AddRange(FindFolders(store, entry.FolderNames));

            var found = nodes.Distinct()
                .Where(n => store.IsLive(n) && store[n].Size >= entry.MinSize && store[n].Size > 0)
                .OrderByDescending(n => store[n].Size)
                .Take(MaxMatchesPerEntry);
            // An item claimed by an earlier entry is not listed twice.
            foreach (int n in found)
                if (taken.Add(n)) matches.Add(new CleanupMatch(entry, n, store[n].Size));
        }
        return matches;
    }

    /// <summary>Total size of the matches without counting a match that lies inside another one twice.</summary>
    public static long TotalSize(NodeStore store, IEnumerable<CleanupMatch> matches) =>
        TreeSync.TopLevel(store, matches.Select(m => m.Node)).Sum(n => store[n].Size);

    internal static string ExpandPattern(string pattern, Func<string, string> expand) =>
        expand(pattern.Replace("{user}", @"%SystemDrive%\Users\*", StringComparison.OrdinalIgnoreCase));

    static bool HasWildcard(string s) => s.Contains('*') || s.Contains('?');

    static bool SegmentMatches(string pattern, string name) =>
        HasWildcard(pattern)
            ? FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)
            : pattern.Equals(name, StringComparison.OrdinalIgnoreCase);

    static string[] Segments(string path) => path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Resolves a path pattern against the tree. The scanned root may lie inside the pattern's path (scanning
    /// C:\Users\me matches C:\Users\*\AppData\…): its own segments are matched against the start of the pattern.
    /// </summary>
    internal static List<int> ResolvePath(NodeStore store, string pattern)
    {
        var result = new List<int>();
        if (store.Root == NodeStore.None) return result;
        string[] pat = Segments(pattern);
        string[] root = Segments(store.RootPath);
        if (root.Length > pat.Length) return result; // the location lies above the scanned folder
        for (int i = 0; i < root.Length; i++)
            if (!SegmentMatches(pat[i], root[i])) return result;

        var current = new List<int> { store.Root };
        for (int i = root.Length; i < pat.Length && current.Count > 0; i++)
        {
            var next = new List<int>();
            foreach (int dir in current)
            {
                if (!store[dir].IsDirectory) continue;
                if (HasWildcard(pat[i]))
                {
                    for (int c = store[dir].FirstChild; c != NodeStore.None; c = store[c].NextSibling)
                        if (SegmentMatches(pat[i], store.GetName(c))) next.Add(c);
                }
                else if (store.FindChild(dir, pat[i]) is int c && c != NodeStore.None) next.Add(c);
            }
            current = next;
        }
        result.AddRange(current);
        return result;
    }

    /// <summary>Folders whose name matches anywhere in the tree, skipping those inside another match.</summary>
    internal static List<int> FindFolders(NodeStore store, List<string> names)
    {
        var found = new List<int>();
        Span<char> buf = stackalloc char[32768];
        int count = store.Count;
        for (int i = 0; i < count; i++)
        {
            ref var n = ref store[i];
            if (!n.IsDirectory || (n.Flags & (NodeFlags.Used | NodeFlags.Deleted)) != NodeFlags.Used || i == store.Root) continue;
            if (Matches(names, store.CopyName(i, buf))) found.Add(i);
        }
        var set = found.ToHashSet();
        return found.Where(f =>
        {
            for (int p = store[f].Parent; p != NodeStore.None; p = store[p].Parent)
                if (set.Contains(p)) return false;
            return true;
        }).ToList();
    }

    static bool Matches(List<string> patterns, ReadOnlySpan<char> name)
    {
        foreach (var p in patterns)
            if (HasWildcard(p) ? FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true) : name.Equals(p, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
