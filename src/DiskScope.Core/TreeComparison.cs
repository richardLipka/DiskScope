namespace DiskScope.Core;

/// <summary>
/// Matches nodes of the current tree to an older one by path, lazily and with caching,
/// to tell how much each file or folder has grown or shrunk since then. The older tree is a
/// <see cref="CompactTree"/>, so two whole-disk scans fit in memory together.
/// </summary>
public sealed class TreeComparison
{
    const int IndexThreshold = 24;

    readonly NodeStore _current;
    // Concurrent, so that rows, the treemap or a background analysis may ask from different threads.
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _match = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<string, int>> _childIndex = [];

    /// <summary>Compares with a full tree (converted to a compact copy).</summary>
    public TreeComparison(NodeStore current, NodeStore baseline) : this(current, CompactTree.From(baseline))
    {
    }

    public TreeComparison(NodeStore current, CompactTree baseline)
    {
        _current = current;
        Baseline = baseline;
        // The baseline may cover a wider tree (e.g. the whole drive while the current scan is one folder).
        _match[current.Root] = baseline.FindPath(current.RootPath);
    }

    public CompactTree Baseline { get; }

    /// <summary>The matching node in the baseline, or <see cref="NodeStore.None"/> when the item is new.</summary>
    public int Match(int node)
    {
        if (_match.TryGetValue(node, out int m)) return m;
        // Walk up to the nearest ancestor already matched, then resolve back down. Iterative, so a very deep tree
        // cannot overflow the stack.
        var chain = new Stack<int>();
        m = NodeStore.None; // no matched ancestor at all (cannot happen while the root is cached): new
        for (int i = node; i != NodeStore.None; i = _current[i].Parent)
        {
            if (_match.TryGetValue(i, out int found))
            {
                m = found;
                break;
            }
            chain.Push(i);
        }
        while (chain.Count > 0)
        {
            int n = chain.Pop();
            m = m == NodeStore.None || !Baseline.IsDirectory(m) ? NodeStore.None : FindChild(m, _current.GetName(n));
            _match[n] = m;
        }
        return m;
    }

    int FindChild(int baseDir, string name)
    {
        if (!_childIndex.TryGetValue(baseDir, out var index))
        {
            if (Baseline.Children(baseDir).Take(IndexThreshold + 1).Count() <= IndexThreshold) return Baseline.FindChild(baseDir, name);
            index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (int c in Baseline.Children(baseDir)) index.TryAdd(Baseline.GetName(c), c);
            index = _childIndex.GetOrAdd(baseDir, index); // built completely before it is shared
        }
        return index.TryGetValue(name, out int found) ? found : NodeStore.None;
    }

    public bool IsNew(int node) => Match(node) == NodeStore.None;

    /// <summary>Growth of the on-disk size since the baseline; a new item counts in full.</summary>
    public long Delta(int node)
    {
        int b = Match(node);
        return _current[node].Size - (b == NodeStore.None ? 0 : Baseline.Size(b));
    }

    /// <summary>Items of the baseline folder that no longer exist, with their former sizes.</summary>
    public List<(string Name, long Size, bool IsDirectory)> Removed(int dir)
    {
        var list = new List<(string, long, bool)>();
        int b = Match(dir);
        if (b == NodeStore.None) return list;
        foreach (int c in Baseline.Children(b))
        {
            string name = Baseline.GetName(c);
            if (_current.FindChild(dir, name) == NodeStore.None) list.Add((name, Baseline.Size(c), Baseline.IsDirectory(c)));
        }
        return list;
    }

    /// <summary>Forgets cached matches, e.g. after the current tree changed by a rescan.</summary>
    public void Reset()
    {
        int root = _match[_current.Root];
        _match.Clear();
        _match[_current.Root] = root;
    }
}
