namespace DiskScope.Core;

/// <summary>Keeps the tree in step with changes made on disk (deletes, shell commands).</summary>
public static class TreeSync
{
    /// <summary>Drops nodes that lie inside another node of the same set, and the root.</summary>
    public static List<int> TopLevel(NodeStore store, IEnumerable<int> nodes)
    {
        var set = nodes.Where(n => n != store.Root).ToHashSet();
        return set.Where(n =>
        {
            for (int p = store[n].Parent; p != NodeStore.None; p = store[p].Parent)
                if (set.Contains(p)) return false;
            return true;
        }).ToList();
    }

    public static bool ExistsOnDisk(NodeStore store, int node)
    {
        string path = LongPath.Of(store.GetPath(node)); // keeps a trailing dot or space in names
        return store[node].IsDirectory ? Directory.Exists(path) : File.Exists(path);
    }

    /// <summary>Removes the nodes that no longer exist on disk; returns those that still do.</summary>
    public static List<int> RemoveVanished(NodeStore store, IEnumerable<int> nodes)
    {
        var remaining = new List<int>();
        foreach (int n in nodes)
        {
            if (!store.IsLive(n)) continue;
            if (ExistsOnDisk(store, n)) remaining.Add(n);
            else store.Remove(n);
        }
        return remaining;
    }

    public static long TotalSize(NodeStore store, IEnumerable<int> nodes) => nodes.Sum(n => store[n].Size);
}
