using DiskScope.Core;
using DiskScope.Core.Scanning;

namespace DiskScope.Tests;

public class ComparisonTests
{
    static NodeStore Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session.Store;
    }

    [Fact]
    public void ReportsGrowthNewAndRemoved()
    {
        using var t = new TestTree();
        t.File(@"grow\a.bin", 10_000);
        t.File(@"same\b.bin", 20_000);
        t.File(@"gone\c.bin", 30_000);
        for (int i = 0; i < 40; i++) t.File($@"many\f{i}.bin", 100);
        var before = Scan(t.Root);

        t.File(@"grow\a2.bin", 50_000);
        Directory.Delete(Path.Combine(t.Root, "gone"), true);
        t.File(@"new\d.bin", 7_000);
        t.File(@"many\F39.BIN", 5_000); // overwrite with different case: same file
        var now = Scan(t.Root);

        var cmp = new TreeComparison(now, before);
        int grow = now.FindPath(Path.Combine(t.Root, "grow"));
        int same = now.FindPath(Path.Combine(t.Root, "same"));
        int fresh = now.FindPath(Path.Combine(t.Root, "new"));
        int f39 = now.FindPath(Path.Combine(t.Root, @"many\f39.bin"));

        Assert.Equal(now[grow].Size - before[before.FindPath(Path.Combine(t.Root, "grow"))].Size, cmp.Delta(grow));
        Assert.True(cmp.Delta(grow) >= 50_000);
        Assert.Equal(0, cmp.Delta(same));
        Assert.True(cmp.IsNew(fresh));
        Assert.Equal(now[fresh].Size, cmp.Delta(fresh));
        Assert.False(cmp.IsNew(f39));
        Assert.True(cmp.Delta(f39) > 0);

        var removed = cmp.Removed(now.Root);
        Assert.Single(removed);
        Assert.Equal("gone", removed[0].Name);
        Assert.True(removed[0].IsDirectory);
    }

    [Fact]
    public void ComparesFolderAgainstWiderBaseline()
    {
        using var t = new TestTree();
        t.File(@"sub\x.bin", 1_000);
        var wide = Scan(t.Root);
        t.File(@"sub\y.bin", 9_000);
        var narrow = Scan(Path.Combine(t.Root, "sub"));

        var cmp = new TreeComparison(narrow, wide);
        Assert.False(cmp.IsNew(narrow.Root));
        Assert.True(cmp.Delta(narrow.Root) >= 9_000);
        Assert.True(cmp.IsNew(narrow.FindPath(Path.Combine(t.Root, @"sub\y.bin"))));
        Assert.False(cmp.IsNew(narrow.FindPath(Path.Combine(t.Root, @"sub\x.bin"))));
    }
}
