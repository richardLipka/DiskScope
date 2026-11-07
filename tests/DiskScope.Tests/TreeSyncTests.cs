using DiskScope.Core;
using DiskScope.Core.Scanning;

namespace DiskScope.Tests;

public class TreeSyncTests
{
    static NodeStore Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session.Store;
    }

    [Fact]
    public void RemovesVanishedAndKeepsExisting()
    {
        using var t = new TestTree();
        string a = t.File("a.bin", 10_000);
        t.File("b.bin", 20_000);
        t.File(@"d\c.bin", 30_000);
        var s = Scan(t.Root);
        int na = s.FindPath(a), nb = s.FindPath(Path.Combine(t.Root, "b.bin")), nd = s.FindPath(Path.Combine(t.Root, "d"));

        File.Delete(a);
        Directory.Delete(Path.Combine(t.Root, "d"), true);
        var remaining = TreeSync.RemoveVanished(s, [na, nb, nd]);

        Assert.Equal([nb], remaining);
        Assert.Equal(1, s[s.Root].FileCount);
        Assert.Equal(0, s[s.Root].DirCount);
        Assert.Equal(20_000, s[s.Root].Length);
        Assert.False(s.IsLive(na));
        Assert.True(s.IsLive(nb));
        Assert.False(s.IsLive(nd));
        Assert.Equal(NodeStore.None, s.FindPath(Path.Combine(t.Root, "d")));
    }

    [Fact]
    public void TopLevelDropsNestedAndRoot()
    {
        using var t = new TestTree();
        t.File(@"d\e\f.bin", 1);
        t.File("g.bin", 1);
        var s = Scan(t.Root);
        int d = s.FindPath(Path.Combine(t.Root, "d"));
        int e = s.FindPath(Path.Combine(t.Root, @"d\e"));
        int f = s.FindPath(Path.Combine(t.Root, @"d\e\f.bin"));
        int g = s.FindPath(Path.Combine(t.Root, "g.bin"));
        Assert.Equal(new[] { d, g }.Order(), TreeSync.TopLevel(s, [s.Root, f, e, d, g]).Order());
    }

    [Fact]
    public void RescanPicksUpChanges()
    {
        using var t = new TestTree();
        t.File(@"d\old.bin", 10_000);
        t.File("outside.bin", 5_000);
        var s = Scan(t.Root);
        int d = s.FindPath(Path.Combine(t.Root, "d"));
        int old = s.FindPath(Path.Combine(t.Root, @"d\old.bin"));

        File.Delete(Path.Combine(t.Root, @"d\old.bin"));
        t.File(@"d\new1.bin", 1_000);
        t.File(@"d\sub\new2.bin", 2_000);
        new DirectoryScanner().Rescan(s, d, new ScanProgress(), CancellationToken.None);

        Assert.False(s.IsLive(old));
        Assert.Equal(3_000, s[d].Length);
        Assert.Equal(2, s[d].FileCount);
        Assert.Equal(1, s[d].DirCount);
        Assert.Equal(8_000, s[s.Root].Length);
        Assert.Equal(3, s[s.Root].FileCount);
        Assert.Equal(2, s[s.Root].DirCount);
        Assert.NotEqual(NodeStore.None, s.FindPath(Path.Combine(t.Root, @"d\sub\new2.bin")));
    }
}
