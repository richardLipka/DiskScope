namespace DiskScope.Core;

/// <summary>
/// Narrows the tree view: hides items smaller than a size and, with name patterns, shows only matching files and
/// the folders that lead to them. A folder whose own name matches is shown with everything inside, so that
/// filtering for "node_modules" still lets you look into each one.
/// </summary>
public sealed class TreeFilter
{
    readonly string[] _patterns;
    readonly HashSet<int> _leadsToMatch = [];
    readonly HashSet<int> _matchedFolders = [];
    readonly Dictionary<int, bool> _insideMatched = [];

    /// <param name="names">Patterns as in search: separated by ';', wildcards or (without them) a substring.</param>
    /// <param name="minSize">Items whose size on disk is below this are hidden (folders by their total).</param>
    public TreeFilter(string? names, long minSize)
    {
        _patterns = Search.ParsePatterns(names);
        MinSize = Math.Max(0, minSize);
    }

    public long MinSize { get; }

    public bool HasNames => _patterns.Length > 0;

    public bool IsActive => HasNames || MinSize > 0;

    /// <summary>Folders that contain a match (or are one), found by <see cref="Prepare"/>.</summary>
    public int FoldersWithMatches => _leadsToMatch.Count;

    /// <summary>
    /// With name patterns, finds which folders lead to a match: one pass over the whole tree, run again when the
    /// tree has changed. Without patterns there is nothing to prepare.
    /// </summary>
    public void Prepare(NodeStore store, CancellationToken cancel = default)
    {
        _leadsToMatch.Clear();
        _matchedFolders.Clear();
        _insideMatched.Clear();
        if (!HasNames || store.Root == NodeStore.None) return;
        int count = store.Count;
        const int RangeSize = 1 << 18;
        var parts = new List<int>[(count + RangeSize - 1) / RangeSize];
        Parallel.For(0, parts.Length, new ParallelOptions { CancellationToken = cancel }, part =>
        {
            var found = new List<int>();
            Span<char> nameBuf = stackalloc char[32768];
            for (int i = part * RangeSize, end = Math.Min(count, (part + 1) * RangeSize); i < end; i++)
            {
                ref var n = ref store[i];
                if ((n.Flags & (NodeFlags.Used | NodeFlags.Deleted)) != NodeFlags.Used || i == store.Root) continue;
                if (!n.IsDirectory && n.Size < MinSize) continue;
                if (Search.Matches(_patterns, store.CopyName(i, nameBuf))) found.Add(i);
            }
            parts[part] = found;
        });
        foreach (var found in parts)
            foreach (int node in found)
            {
                cancel.ThrowIfCancellationRequested();
                if (store[node].IsDirectory) _matchedFolders.Add(node);
                // Mark the way up; stop where an earlier match already marked it.
                for (int p = store[node].IsDirectory ? node : store[node].Parent; p != NodeStore.None && _leadsToMatch.Add(p); p = store[p].Parent) { }
            }
    }

    /// <summary>Whether a node is shown. The view root is always shown by the caller.</summary>
    public bool Shows(NodeStore store, int node)
    {
        ref var n = ref store[node];
        if (n.Size < MinSize) return false;
        if (!HasNames) return true;
        if (n.IsDirectory && _leadsToMatch.Contains(node)) return true;
        int parent = n.Parent;
        if (parent != NodeStore.None && InsideMatchedFolder(store, parent)) return true;
        return !n.IsDirectory && _leadsToMatch.Contains(parent) && MatchesName(store, node);
    }

    bool MatchesName(NodeStore store, int node)
    {
        Span<char> nameBuf = stackalloc char[32768];
        return Search.Matches(_patterns, store.CopyName(node, nameBuf));
    }

    /// <summary>Whether the folder is a matched folder or lies inside one (cached; siblings share their parent).</summary>
    bool InsideMatchedFolder(NodeStore store, int dir)
    {
        if (_insideMatched.TryGetValue(dir, out bool known)) return known;
        bool inside = false;
        for (int p = dir; p != NodeStore.None; p = store[p].Parent)
            if (_matchedFolders.Contains(p))
            {
                inside = true;
                break;
            }
        _insideMatched[dir] = inside;
        return inside;
    }
}
