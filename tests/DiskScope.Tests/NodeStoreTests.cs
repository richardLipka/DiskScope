using DiskScope.Core;

namespace DiskScope.Tests;

public class NodeStoreTests
{
    [Fact]
    public void StoresLatin1AndUnicodeNames()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int a = s.Add(root, "café.txt", NodeFlags.None, 0, 1, 1, 0);
        int b = s.Add(root, "Příliš žluťoučký.doc", NodeFlags.None, 0, 1, 1, 0);
        int c = s.Add(root, "日本語", NodeFlags.Directory, 0, 0, 0, 0);
        Assert.Equal("café.txt", s.GetName(a));
        Assert.Equal("Příliš žluťoučký.doc", s.GetName(b));
        Assert.Equal("日本語", s.GetName(c));
        Assert.True(s.NameEquals(a, "CAFÉ.TXT"));
        Assert.True(s.NameEquals(b, "příliš ŽLUŤOUČKÝ.DOC"));
    }

    [Fact]
    public void BuildsPathsAndFindsThem()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"D:\Data");
        int dir = s.Add(root, "sub", NodeFlags.Directory, 0, 0, 0, 0);
        int file = s.Add(dir, "f.bin", NodeFlags.None, 0, 10, 10, 0);
        s.PublishChildren(root, dir);
        s.PublishChildren(dir, file);
        Assert.Equal(@"D:\Data\sub\f.bin", s.GetPath(file));
        Assert.Equal(file, s.FindPath(@"d:\data\SUB\f.bin"));
        Assert.Equal(root, s.FindPath(@"D:\Data\"));
        Assert.Equal(NodeStore.None, s.FindPath(@"D:\Other"));

        var drive = new NodeStore();
        int r = drive.CreateRoot(@"C:\");
        int w = drive.Add(r, "Windows", NodeFlags.Directory, 0, 0, 0, 0);
        drive.PublishChildren(r, w);
        Assert.Equal(@"C:\", drive.GetPath(r));
        Assert.Equal(@"C:\Windows", drive.GetPath(w));
        Assert.Equal(w, drive.FindPath(@"C:\Windows"));
    }

    [Fact]
    public void RemoveSubtractsFromAncestors()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int dir = s.Add(root, "d", NodeFlags.Directory, 0, 0, 0, 0);
        int f1 = s.Add(dir, "1", NodeFlags.None, 0, 100, 90, 0);
        int f2 = s.Add(dir, "2", NodeFlags.None, 0, 200, 190, 0);
        s[f1].NextSibling = f2;
        s.PublishChildren(dir, f1);
        s.PublishChildren(root, dir);
        s.AddToAncestors(dir, 300, 280, 2, 0);
        s.AddToAncestors(root, 0, 0, 0, 1);

        s.Remove(f1);
        Assert.Equal(200, s[root].Size);
        Assert.Equal(1, s[root].FileCount);
        Assert.Equal(f2, s[dir].FirstChild);

        s.Remove(dir);
        Assert.Equal(0, s[root].Size);
        Assert.Equal(0, s[root].FileCount);
        Assert.Equal(0, s[root].DirCount);
        Assert.Equal(NodeStore.None, s[root].FirstChild);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(-5L)]
    [InlineData(2650467744000000000L)] // one tick after DateTime.MaxValue
    public void OutOfRangeTimesBecomeUnknownAndNeverThrow(long bad)
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\", bad);
        int f = s.Add(root, "weird.bin", NodeFlags.None, 0, 1, 1, bad, bad);
        s.PublishChildren(root, f);
        s.MaxTimesToAncestors(root, bad, bad);
        Assert.Equal(0, s[root].MTime);
        Assert.Equal(0, s[f].MTime);
        Assert.Equal(0, s[f].ATime);
        Assert.Null(Format.FileTime(bad));
        // Analyses that turn times into dates do not throw either.
        _ = new DiskScope.Core.Orphans.OrphanFinder().Find(s, [new("x", null, null)], new HashSet<string>(), [], DateTime.UtcNow);
        Assert.Empty(Search.Run(s, new SearchQuery { ModifiedBefore = DateTime.UtcNow }).Nodes);
    }

    [Fact]
    public void ValidTimesAreKept()
    {
        long t = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.Equal(t, Format.ValidFileTime(t));
        Assert.Equal(new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), Format.FileTime(t));
    }

    [Fact]
    public void ManyNodesCrossChunkBoundaries()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        Parallel.For(0, 200_000, i => s.Add(root, "file_with_a_longer_name_" + i, NodeFlags.None, 0, i, i, 0));
        Assert.Equal(200_001, s.Count);
        var names = new HashSet<string>();
        for (int i = 1; i < s.Count; i++) names.Add(s.GetName(i));
        Assert.Equal(200_000, names.Count);
    }
}
