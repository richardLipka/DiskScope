using System.IO.Enumeration;

namespace DiskScope.Core;

public enum SearchKind
{
    Files,
    /// <summary>Folders ranked by the size of the files directly inside them (not their subfolders).</summary>
    FoldersOwnContent,
    /// <summary>Folders ranked by their whole subtree.</summary>
    Folders,
    All,
}

/// <summary>Criteria for finding items in a scanned tree. Every criterion left at its default matches everything.</summary>
public sealed record SearchQuery
{
    /// <summary>Name patterns separated by ';' — wildcards (* ?) or, without them, a substring. Case-insensitive.</summary>
    public string? Name { get; init; }
    public SearchKind Kind { get; init; } = SearchKind.Files;
    public long MinSize { get; init; }
    public long MaxSize { get; init; } = long.MaxValue;
    /// <summary>Only items last modified before this time (UTC).</summary>
    public DateTime? ModifiedBefore { get; init; }
    public DateTime? ModifiedAfter { get; init; }
    /// <summary>Only items last accessed before this time (UTC); items with an unknown access time do not match.</summary>
    public DateTime? AccessedBefore { get; init; }
    /// <summary>Search only under this node; the root when <see cref="NodeStore.None"/>.</summary>
    public int Scope { get; init; } = NodeStore.None;
    /// <summary>How many of the largest matches to return.</summary>
    public int Limit { get; init; } = 1000;
}

public sealed record SearchResult(List<int> Nodes, long TotalMatches, long TotalSize, TimeSpan Elapsed);

public static class Search
{
    /// <summary>Size used for ranking: the on-disk size, or for <see cref="SearchKind.FoldersOwnContent"/> the folder's own files.</summary>
    public static long RankSize(NodeStore store, int node, SearchKind kind)
    {
        ref var n = ref store[node];
        if (kind != SearchKind.FoldersOwnContent || !n.IsDirectory) return n.Size;
        long own = n.Size;
        for (int c = n.FirstChild; c != NodeStore.None; c = store[c].NextSibling)
            if (store[c].IsDirectory) own -= store[c].Size;
        return own;
    }

    public static SearchResult Run(NodeStore store, SearchQuery q, CancellationToken cancel = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var patterns = ParsePatterns(q.Name);
        long fromTime = q.ModifiedAfter?.ToFileTimeUtc() ?? long.MinValue;
        long toTime = q.ModifiedBefore?.ToFileTimeUtc() ?? long.MaxValue;
        long accessedBefore = q.AccessedBefore?.ToFileTimeUtc() ?? long.MaxValue;
        int scope = q.Scope == NodeStore.None ? store.Root : q.Scope;

        // Ranges of the node table are searched in parallel, each keeping its own top list; then merged.
        int count = store.Count;
        const int RangeSize = 1 << 18;
        var parts = new (PriorityQueue<int, long> Heap, long Matches, long Total)[(count + RangeSize - 1) / RangeSize];
        Parallel.For(0, parts.Length, new ParallelOptions { CancellationToken = cancel }, part =>
            parts[part] = SearchRange(store, q, patterns, fromTime, toTime, accessedBefore, scope,
                part * RangeSize, Math.Min(count, (part + 1) * RangeSize), cancel));

        var heap = new PriorityQueue<int, long>(q.Limit + 1);
        long matches = 0, total = 0;
        foreach (var (partHeap, partMatches, partTotal) in parts)
        {
            matches += partMatches;
            total += partTotal;
            while (partHeap.TryDequeue(out int node, out long rank)) Offer(heap, node, rank, q.Limit);
        }

        var nodes = new List<int>(heap.Count);
        while (heap.TryDequeue(out int node, out _)) nodes.Add(node);
        nodes.Reverse();
        return new SearchResult(nodes, matches, total, clock.Elapsed);
    }

    static void Offer(PriorityQueue<int, long> heap, int node, long rank, int limit)
    {
        if (heap.Count < limit) heap.Enqueue(node, rank);
        else if (limit > 0 && heap.TryPeek(out _, out long smallest) && rank > smallest) heap.EnqueueDequeue(node, rank);
    }

    static (PriorityQueue<int, long>, long, long) SearchRange(NodeStore store, SearchQuery q, string[] patterns, long fromTime, long toTime,
        long accessedBefore, int scope, int from, int to, CancellationToken cancel)
    {
        // Min-heap of the best matches so far, keyed by rank size.
        var heap = new PriorityQueue<int, long>(q.Limit + 1);
        long matches = 0, total = 0;
        bool wholeTree = scope == store.Root;
        Span<char> nameBuf = stackalloc char[32768];

        for (int i = from; i < to; i++)
        {
            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            ref var n = ref store[i];
            if ((n.Flags & (NodeFlags.Used | NodeFlags.Deleted)) != NodeFlags.Used || i == store.Root) continue;
            bool dir = n.IsDirectory;
            if (q.Kind == SearchKind.Files ? dir : q.Kind is SearchKind.Folders or SearchKind.FoldersOwnContent && !dir) continue;
            if (n.MTime < fromTime || n.MTime > toTime) continue;
            if (accessedBefore != long.MaxValue && (n.ATime == 0 || n.ATime > accessedBefore)) continue;
            if (!dir && (n.Size < q.MinSize || n.Size > q.MaxSize)) continue;
            if (dir && q.Kind != SearchKind.FoldersOwnContent && (n.Size < q.MinSize || n.Size > q.MaxSize)) continue;
            if (patterns.Length > 0 && !Matches(patterns, store.CopyName(i, nameBuf))) continue;
            long rank = RankSize(store, i, q.Kind);
            if (q.Kind == SearchKind.FoldersOwnContent && (rank < q.MinSize || rank > q.MaxSize)) continue;
            if (!wholeTree && !IsUnder(store, i, scope)) continue;

            matches++;
            total += rank;
            Offer(heap, i, rank, q.Limit);
        }
        return (heap, matches, total);
    }

    static bool IsUnder(NodeStore store, int node, int scope)
    {
        for (int p = store[node].Parent; p != NodeStore.None; p = store[p].Parent)
            if (p == scope) return true;
        return false;
    }

    static string[] ParsePatterns(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Contains('*') || p.Contains('?') ? p : "*" + p + "*").ToArray();

    static bool Matches(string[] patterns, ReadOnlySpan<char> name)
    {
        foreach (var p in patterns)
            if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) return true;
        return false;
    }
}
