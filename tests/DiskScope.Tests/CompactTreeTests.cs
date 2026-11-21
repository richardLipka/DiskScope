using DiskScope.Core;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;

namespace DiskScope.Tests;

/// <summary>The compact tree used as the older scan in comparisons must describe exactly what the full tree does.</summary>
public class CompactTreeTests
{
    static NodeStore Build()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\data");
        int head = NodeStore.None;
        int Add(int parent, string name, bool dir, long size, ref int h)
        {
            int n = s.Add(parent, name, dir ? NodeFlags.Directory | NodeFlags.Scanned : NodeFlags.None, 0, size, size, 1);
            s[n].NextSibling = h;
            h = n;
            return n;
        }
        int docs = Add(root, "Docs", true, 0, ref head);
        int empty = Add(root, "Empty", true, 0, ref head);
        Add(root, "top.bin", false, 4096, ref head);
        s.PublishChildren(root, head);
        int h2 = NodeStore.None;
        Add(docs, "Žluťoučký kůň.txt", false, 8192, ref h2);
        int deep = Add(docs, "Deep", true, 0, ref h2);
        Add(docs, "文件.doc", false, 12288, ref h2);
        s.PublishChildren(docs, h2);
        int h3 = NodeStore.None;
        Add(deep, "x.dat", false, 1 << 20, ref h3);
        s.PublishChildren(deep, h3);
        s.PublishChildren(empty, NodeStore.None);
        s.ComputeTotalsFromRoot();
        return s;
    }

    static void AssertSame(NodeStore full, int f, CompactTree compact, int c)
    {
        Assert.Equal(full.GetName(f), compact.GetName(c));
        Assert.Equal(full[f].Size, compact.Size(c));
        Assert.Equal(full[f].IsDirectory, compact.IsDirectory(c));
        var fc = new List<int>();
        for (int x = full[f].FirstChild; x != NodeStore.None; x = full[x].NextSibling) fc.Add(x);
        var cc = compact.Children(c).ToList();
        Assert.Equal(fc.Count, cc.Count);
        for (int i = 0; i < fc.Count; i++) AssertSame(full, fc[i], compact, cc[i]);
    }

    [Fact]
    public void CopyOfAFullTreeMatchesIt()
    {
        var s = Build();
        var compact = CompactTree.From(s);
        Assert.Equal(8, compact.Count);
        AssertSame(s, s.Root, compact, CompactTree.Root);
        Assert.Equal(compact.Count, compact.Span(CompactTree.Root));
    }

    [Fact]
    public void LoadingASnapshotCompactlyGivesTheSameTreeAsLoadingItFully()
    {
        using var t = new TestTree();
        t.File("a.bin", 1234);
        t.File(@"Žluťoučký\kůň.txt", 99);
        t.File(@"sub\deep\deeper\x.dat", 70_000);
        t.File(@"sub\deep\y.dat", 5000);
        t.Dir(@"sub\empty");
        var session = new ScanSession(t.Root, ScanMode.Directory);
        session.Start();
        session.Wait();
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Snapshot.Save(session.Store, file, session.StartedUtc, session.Elapsed, session.Mode, true, 0);
            var full = Snapshot.Load(file, out _);
            var compact = Snapshot.LoadCompact(file, out var info);
            Assert.Equal(info.Nodes, compact.Count);
            AssertSame(full, full.Root, compact, CompactTree.Root);
            Assert.Equal(full[full.Root].Size, compact.Size(CompactTree.Root));
            Assert.NotEqual(NodeStore.None, compact.FindPath(Path.Combine(t.Root, @"Žluťoučký\kůň.txt")));
            Assert.Equal(NodeStore.None, compact.FindPath(Path.Combine(t.Root, "missing")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ComparisonGivesTheSameAnswersWithACompactBaseline()
    {
        var before = Build();
        var now = Build();
        // Grow a file and remove a folder in the current tree.
        int docs = now.FindPath(@"C:\data\Docs");
        int deep = now.FindPath(@"C:\data\Docs\Deep");
        int doc = now.FindPath(@"C:\data\Docs\文件.doc");
        now.AddToAncestors(docs, 100_000, 0, 0, 0);
        now[doc].Size += 100_000;
        now.Remove(deep);

        var cmp = new TreeComparison(now, CompactTree.From(before));
        Assert.Equal(100_000 - (1 << 20), cmp.Delta(now.Root));
        Assert.Equal(100_000, cmp.Delta(doc));
        Assert.False(cmp.IsNew(doc));
        var removed = cmp.Removed(docs);
        Assert.Single(removed);
        Assert.Equal("Deep", removed[0].Name);
        Assert.True(removed[0].IsDirectory);
        Assert.Equal(1 << 20, removed[0].Size);
    }

    [Fact]
    public void ADamagedFileIsRejected()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        try
        {
            var s = Build();
            Snapshot.Save(s, file, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
            var bytes = File.ReadAllBytes(file);
            Array.Resize(ref bytes, bytes.Length - 20); // cut off the end of the tree
            File.WriteAllBytes(file, bytes);
            Assert.ThrowsAny<Exception>(() => Snapshot.LoadCompact(file, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
