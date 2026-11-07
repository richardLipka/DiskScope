namespace DiskScope.Core;

public enum SortColumn
{
    Name,
    Size,
    Length,
    Files,
    Modified,
    Accessed,
    Delta,
}

/// <summary>
/// The tree flattened into the rows currently visible (the view root and its expanded descendants),
/// which is what a virtual list view needs. Children are sorted when the rows are rebuilt.
/// </summary>
public sealed class TreeRows(NodeStore store)
{
    readonly HashSet<int> _expanded = [];
    readonly List<int> _nodes = [];
    readonly List<int> _depths = [];
    readonly List<int> _scratch = [];
    readonly List<int> _previous = [];
    int _viewRoot = NodeStore.None;

    public NodeStore Store => store;
    public SortColumn Sort { get; set; } = SortColumn.Size;
    public bool Descending { get; set; } = true;

    /// <summary>Optional value used by <see cref="SortColumn.Delta"/>.</summary>
    public Func<int, long>? DeltaOf { get; set; }

    /// <summary>Optional filter; a child for which it returns false is hidden.</summary>
    public Func<int, bool>? Filter { get; set; }

    public int Count => _nodes.Count;
    public int NodeAt(int row) => _nodes[row];
    public int DepthAt(int row) => _depths[row];

    public int ViewRoot
    {
        get => _viewRoot == NodeStore.None ? store.Root : _viewRoot;
        set
        {
            _viewRoot = value;
            _expanded.Add(value);
        }
    }

    public bool IsExpanded(int node) => _expanded.Contains(node);

    public bool HasChildren(int node) => store[node].IsDirectory && Volatile.Read(ref store[node].FirstChild) != NodeStore.None;

    public void SetExpanded(int node, bool expanded)
    {
        if (expanded) _expanded.Add(node); else _expanded.Remove(node);
    }

    /// <summary>Expands all ancestors of a node and moves the view root up if the node lies outside it.</summary>
    public void Reveal(int node)
    {
        int root = ViewRoot;
        bool inside = false;
        for (int i = node; i != NodeStore.None; i = store[i].Parent)
            if (i == root) { inside = true; break; }
        if (!inside) ViewRoot = store.Root;
        for (int i = store[node].Parent; i != NodeStore.None; i = store[i].Parent) _expanded.Add(i);
    }

    public int IndexOf(int node) => _nodes.IndexOf(node);

    /// <summary>
    /// Rows of many nodes at once, in one pass over the rows (−1 for nodes not shown). Restoring a selection of
    /// thousands of items with <see cref="IndexOf"/> would scan millions of rows once per item.
    /// </summary>
    public List<int> RowsOf(IReadOnlyCollection<int> nodes)
    {
        var rows = new List<int>(nodes.Count);
        if (nodes.Count == 0) return rows;
        if (nodes.Count == 1)
        {
            int row = IndexOf(nodes.First());
            if (row >= 0) rows.Add(row);
            return rows;
        }
        var wanted = nodes as IReadOnlySet<int> ?? new HashSet<int>(nodes);
        for (int i = 0; i < _nodes.Count && rows.Count < wanted.Count; i++)
            if (wanted.Contains(_nodes[i])) rows.Add(i);
        return rows;
    }

    /// <summary>Recomputes the visible rows; returns false when they came out the same as before.</summary>
    public bool Rebuild()
    {
        _previous.Clear();
        _previous.AddRange(_nodes);
        _nodes.Clear();
        _depths.Clear();
        if (store.Root == NodeStore.None) return _previous.Count != 0;
        if (_viewRoot == NodeStore.None) ViewRoot = store.Root;
        AddRows(ViewRoot);
        return !_nodes.SequenceEqual(_previous);
    }

    /// <summary>
    /// Adds a node and its expanded descendants in display order. Iterative: revealing an item in a very deep
    /// folder expands every level above it, and a recursive walk could overflow the UI thread's stack.
    /// </summary>
    void AddRows(int root)
    {
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            _nodes.Add(node);
            _depths.Add(depth);
            if (!_expanded.Contains(node)) continue;
            var children = SortedChildren(node);
            for (int i = children.Length - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
    }

    public int[] SortedChildren(int node)
    {
        _scratch.Clear();
        for (int c = Volatile.Read(ref store[node].FirstChild); c != NodeStore.None; c = store[c].NextSibling)
            if (Filter == null || Filter(c)) _scratch.Add(c);
        var children = _scratch.ToArray();
        if (Sort == SortColumn.Name)
        {
            // Names never change, so comparing them live is safe.
            var cmp = CompareNames(children);
            Array.Sort(children, Descending ? (a, b) => cmp(b, a) : cmp);
            return children;
        }
        // Sizes, counts and times change while a scan or a deletion runs; comparing live values could give the sort
        // inconsistent answers (which makes Array.Sort throw), so each key is read once before sorting.
        var keys = new long[children.Length];
        for (int i = 0; i < children.Length; i++) keys[i] = SortKey(children[i]);
        Array.Sort(keys, children);
        if (Descending) Array.Reverse(children);
        return children;
    }

    long SortKey(int n) => Sort switch
    {
        SortColumn.Length => store[n].Length,
        SortColumn.Files => FileCount(n),
        SortColumn.Modified => store[n].MTime,
        SortColumn.Accessed => store[n].ATime,
        SortColumn.Delta when DeltaOf != null => DeltaOf(n),
        _ => store[n].Size,
    };

    int FileCount(int n) => store[n].IsDirectory ? store[n].FileCount : 1;

    Comparison<int> CompareNames(int[] children)
    {
        var names = new Dictionary<int, string>(children.Length);
        foreach (int c in children) names[c] = store.GetName(c);
        return (a, b) =>
        {
            int dirs = store[b].IsDirectory.CompareTo(store[a].IsDirectory);
            if (dirs != 0) return Descending ? -dirs : dirs;
            return StringComparer.CurrentCultureIgnoreCase.Compare(names[a], names[b]);
        };
    }
}
