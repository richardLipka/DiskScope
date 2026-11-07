namespace DiskScope.Core;

/// <summary>
/// What a folder holds, condensed: its largest entries, the file types taking the most space and the
/// largest files anywhere inside. Used to describe a folder to someone (or an AI) who cannot see the disk.
/// </summary>
public sealed record FolderSummary(
    int Node,
    List<int> LargestChildren,
    List<(string Extension, long Size, int Count)> Extensions,
    List<int> LargestFiles,
    bool Truncated)
{
    /// <summary>Walking stops after this many entries, so a summary of a whole drive stays quick.</summary>
    public const int MaxVisited = 2_000_000;

    public static FolderSummary Build(NodeStore store, int node, int top = 12)
    {
        var children = new List<int>();
        if (store[node].IsDirectory)
            for (int c = store[node].FirstChild; c != NodeStore.None; c = store[c].NextSibling) children.Add(c);
        store.SortBySizeDescending(children);
        if (children.Count > top) children.RemoveRange(top, children.Count - top);

        var extensions = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        var largest = new PriorityQueue<int, long>();
        var stack = new Stack<int>();
        stack.Push(node);
        int visited = 0;
        bool truncated = false;
        while (stack.Count > 0)
        {
            if (++visited > MaxVisited)
            {
                truncated = true;
                break;
            }
            int n = stack.Pop();
            ref var e = ref store[n];
            if (e.IsDirectory)
            {
                for (int c = e.FirstChild; c != NodeStore.None; c = store[c].NextSibling) stack.Push(c);
                continue;
            }
            string ext = Path.GetExtension(store.GetName(n)); // "" for files without one
            var (size, count) = extensions.GetValueOrDefault(ext);
            extensions[ext] = (size + e.Size, count + 1);
            largest.Enqueue(n, e.Size);
            if (largest.Count > top) largest.Dequeue();
        }

        var exts = extensions.OrderByDescending(x => x.Value.Size).Take(top).Select(x => (x.Key, x.Value.Size, x.Value.Count)).ToList();
        var files = new List<int>();
        while (largest.TryDequeue(out int f, out _)) files.Add(f);
        files.Reverse();
        return new FolderSummary(node, children, exts, files, truncated);
    }
}
