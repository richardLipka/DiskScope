using DiskScope.Core;
using DiskScope.Core.Cleanup;
using DiskScope.Core.Duplicates;
using DiskScope.Core.Orphans;
using DiskScope.Core.Storage;

namespace DiskScope.Tests;

/// <summary>
/// Every part of the app that walks the tree must cope with very deep trees. The checks run on a thread with a
/// small stack, so any recursion that grows with the depth overflows at once (and fails the run).
/// </summary>
public class DeepTreeTests
{
    const int Depth = 30_000;

    static (NodeStore Store, int Deepest) DeepStore(string root = @"C:\deep")
    {
        var s = new NodeStore();
        int parent = s.CreateRoot(root);
        for (int i = 0; i < Depth; i++)
        {
            int dir = s.Add(parent, "d", NodeFlags.Directory, 0, 0, 0, 0);
            s.PublishChildren(parent, dir);
            parent = dir;
        }
        int file = s.Add(parent, "bottom.bin", NodeFlags.None, 0, 4096, 4000, 1);
        s.PublishChildren(parent, file);
        s.ComputeTotalsFromRoot();
        return (s, file);
    }

    static void OnSmallStack(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "timed out");
        if (error != null) throw new Xunit.Sdk.XunitException("Failed on a small stack: " + error);
    }

    [Fact]
    public void TreeRowsRevealAndRebuildAtDepth() => OnSmallStack(() =>
    {
        var (s, deepest) = DeepStore();
        var rows = new TreeRows(s);
        rows.Reveal(deepest); // "Show in tree" from a search result expands every level
        rows.Rebuild();
        Assert.Equal(Depth + 2, rows.Count);
        Assert.Equal(deepest, rows.NodeAt(rows.Count - 1));
        Assert.Equal(Depth + 1, rows.DepthAt(rows.Count - 1));
    });

    [Fact]
    public void ComparisonAtDepth() => OnSmallStack(() =>
    {
        var (now, deepest) = DeepStore();
        var (before, _) = DeepStore();
        var cmp = new TreeComparison(now, before);
        Assert.False(cmp.IsNew(deepest));
        Assert.Equal(0, cmp.Delta(deepest));
        var rows = new TreeRows(now) { DeltaOf = cmp.Delta, Filter = n => cmp.Delta(n) >= 0 };
        rows.Reveal(deepest);
        rows.Rebuild();
        Assert.Equal(Depth + 2, rows.Count);
    });

    [Fact]
    public void SnapshotAtDepth() => OnSmallStack(() =>
    {
        var (s, _) = DeepStore();
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Snapshot.Save(s, file, DateTime.UtcNow, TimeSpan.Zero, Core.Scanning.ScanMode.Directory, true, 0);
            var loaded = Snapshot.Load(file, out var info);
            Assert.Equal(Depth + 2, info.Nodes);
            Assert.Equal(4096, loaded[loaded.Root].Size);
            Assert.Equal(Depth, loaded[loaded.Root].DirCount);
        }
        finally
        {
            File.Delete(file);
        }
    });

    [Fact]
    public void AnalysesAtDepth() => OnSmallStack(() =>
    {
        var (s, deepest) = DeepStore();
        int middle = s[s[deepest].Parent].Parent;
        for (int i = 0; i < Depth / 2; i++) middle = s[middle].Parent;

        Assert.Equal([deepest], Search.Run(s, new SearchQuery { Name = "bottom*", Scope = middle }).Nodes);
        Assert.Equal(@"C:\deep".Length + Depth * @"\d".Length + @"\bottom.bin".Length, s.GetPath(deepest).Length);
        Assert.Equal(deepest, s.FindPath(s.GetPath(deepest)));
        Assert.Single(FolderSummary.Build(s, s.Root).LargestFiles);
        Assert.NotEmpty(TreemapLayout.Build(s, s.Root, new RectF(0, 0, 800, 600)));
        Assert.Equal([s[s.Root].FirstChild], TreeSync.TopLevel(s, [deepest, s[s.Root].FirstChild]));
        _ = CleanupFinder.Find(s, CleanupCatalog.Default());
        _ = new OrphanFinder().Find(s, [new InstalledProgram("x", null, null)], new HashSet<string> { "a_b" }, [], DateTime.UtcNow);
        _ = new DuplicateFinder { MinSize = 1, Scope = middle }.Find(s, new DuplicateProgress());

        s.Remove(s[s.Root].FirstChild); // drops the whole chain
        Assert.Equal(0, s[s.Root].Size);
        Assert.False(s.IsLive(deepest));
    });
}
