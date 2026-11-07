using DiskScope.Core;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;
using Xunit.Abstractions;

namespace DiskScope.Tests;

public class SnapshotTests(ITestOutputHelper output)
{
    static ScanSession Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session;
    }

    static void AssertSameTree(NodeStore a, int na, NodeStore b, int nb)
    {
        Assert.Equal(a.GetName(na), b.GetName(nb));
        Assert.Equal(a[na].Size, b[nb].Size);
        Assert.Equal(a[na].Length, b[nb].Length);
        Assert.Equal(a[na].FileCount, b[nb].FileCount);
        Assert.Equal(a[na].DirCount, b[nb].DirCount);
        Assert.Equal(a[na].MTime, b[nb].MTime);
        Assert.Equal(a[na].Flags & ~NodeFlags.Used, b[nb].Flags & ~NodeFlags.Used);
        var ca = new List<int>();
        for (int c = a[na].FirstChild; c != NodeStore.None; c = a[c].NextSibling) ca.Add(c);
        var cb = new List<int>();
        for (int c = b[nb].FirstChild; c != NodeStore.None; c = b[c].NextSibling) cb.Add(c);
        Assert.Equal(ca.Count, cb.Count);
        for (int i = 0; i < ca.Count; i++) AssertSameTree(a, ca[i], b, cb[i]);
    }

    [Fact]
    public void RoundTripsATree()
    {
        using var t = new TestTree();
        t.File("a.bin", 1234);
        t.File(@"Žluťoučký\kůň.txt", 99);
        t.File(@"sub\deep\deeper\x.dat", 70_000);
        t.Dir(@"sub\empty");
        var session = Scan(t.Root);
        string file = Path.Combine(t.Root, "..", Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Snapshot.Save(session.Store, file, session.StartedUtc, session.Elapsed, session.Mode, true, 3);
            var loaded = Snapshot.Load(file, out var info);
            Assert.Equal(session.RootPath, info.RootPath);
            Assert.Equal(3, info.Files);
            Assert.Equal(5, info.Directories);
            Assert.Equal(3, info.Errors);
            Assert.Equal(9, info.Nodes);
            AssertSameTree(session.Store, session.Store.Root, loaded, loaded.Root);
            Assert.NotEqual(NodeStore.None, loaded.FindPath(Path.Combine(t.Root, @"Žluťoučký\kůň.txt")));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SkipsRemovedNodes()
    {
        using var t = new TestTree();
        t.File("keep.bin", 100);
        t.File(@"gone\x.bin", 100);
        var session = Scan(t.Root);
        var s = session.Store;
        s.Remove(s.FindPath(Path.Combine(t.Root, "gone")));
        string file = Path.Combine(t.Root, "..", Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Snapshot.Save(s, file, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
            var loaded = Snapshot.Load(file, out var info);
            Assert.Equal(2, info.Nodes);
            Assert.Equal(1, loaded[loaded.Root].FileCount);
            Assert.Equal(0, loaded[loaded.Root].DirCount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CatalogListsAndDeletes()
    {
        using var t = new TestTree();
        t.File("f.bin", 10);
        var session = Scan(t.Root);
        var catalog = new SnapshotCatalog(t.Dir("snaps"));
        var older = DateTime.UtcNow.AddDays(-1);
        Snapshot.Save(session.Store, catalog.NewFilePath(session.RootPath, older), older, TimeSpan.Zero, ScanMode.Directory, true, 0);
        Snapshot.Save(session.Store, catalog.NewFilePath(session.RootPath, DateTime.UtcNow), DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
        File.WriteAllText(Path.Combine(catalog.Directory, "junk" + Snapshot.Extension), "not a snapshot");

        Assert.Equal(2, catalog.ListFor(session.RootPath).Count);
        Assert.True(catalog.LatestFor(session.RootPath)!.ScanTimeUtc > older);
        catalog.Delete(catalog.LatestFor(session.RootPath)!);
        Assert.Single(catalog.List());
        catalog.DeleteAll();
        Assert.Empty(System.IO.Directory.GetFiles(catalog.Directory));
    }

    [Fact]
    public void SavesAndLoadsLargeTreeQuickly()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var session = Scan(win);
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Snapshot.Save(session.Store, file, session.StartedUtc, session.Elapsed, session.Mode, true, 0);
            var save = sw.Elapsed;
            sw.Restart();
            var loaded = Snapshot.Load(file, out var info);
            output.WriteLine($"{info.Nodes:N0} nodes, file {new FileInfo(file).Length / 1024:N0} KB, save {save.TotalMilliseconds:0} ms, load {sw.Elapsed.TotalMilliseconds:0} ms");
            AssertSameTree(session.Store, session.Store.Root, loaded, loaded.Root);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
