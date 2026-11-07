using DiskScope.Core;
using DiskScope.Core.Scanning;
using Xunit.Abstractions;

namespace DiskScope.Tests;

public class SearchTests(ITestOutputHelper output)
{
    static NodeStore Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session.Store;
    }

    static List<string> Names(NodeStore s, SearchResult r) => r.Nodes.Select(s.GetName).ToList();

    [Fact]
    public void FindsLargestFilesByPatternSizeAndScope()
    {
        using var t = new TestTree();
        t.File(@"a\movie.mkv", 900_000);
        t.File(@"a\notes.txt", 1_000);
        t.File(@"b\backup.zip", 500_000);
        t.File(@"b\deep\old.log", 300_000);
        t.File(@"b\deep\new.LOG", 200_000);
        t.File("Příloha.pdf", 50_000);
        var s = Scan(t.Root);

        Assert.Equal(["movie.mkv", "backup.zip", "old.log", "new.LOG", "Příloha.pdf", "notes.txt"], Names(s, Search.Run(s, new SearchQuery())));
        Assert.Equal(["movie.mkv", "backup.zip"], Names(s, Search.Run(s, new SearchQuery { Limit = 2 })));

        var logs = Search.Run(s, new SearchQuery { Name = "*.log" });
        Assert.Equal(["old.log", "new.LOG"], Names(s, logs));
        Assert.Equal(2, logs.TotalMatches);

        Assert.Equal(["movie.mkv", "backup.zip"], Names(s, Search.Run(s, new SearchQuery { Name = "*.mkv; *.zip" })));
        Assert.Equal(["Příloha.pdf"], Names(s, Search.Run(s, new SearchQuery { Name = "příl" })));
        Assert.Equal(["movie.mkv", "backup.zip"], Names(s, Search.Run(s, new SearchQuery { MinSize = 400_000 })));
        Assert.Equal(["notes.txt"], Names(s, Search.Run(s, new SearchQuery { MaxSize = 10_000 })));

        int b = s.FindPath(Path.Combine(t.Root, "b"));
        Assert.Equal(["backup.zip", "old.log", "new.LOG"], Names(s, Search.Run(s, new SearchQuery { Scope = b })));
    }

    [Fact]
    public void RanksFoldersByOwnContentOrSubtree()
    {
        using var t = new TestTree();
        t.File(@"parent\small.bin", 10_000);
        t.File(@"parent\child\big.bin", 800_000);
        t.File(@"other\mid.bin", 300_000);
        var s = Scan(t.Root);

        Assert.Equal(["parent", "child", "other"], Names(s, Search.Run(s, new SearchQuery { Kind = SearchKind.Folders })));
        Assert.Equal(["child", "other", "parent"], Names(s, Search.Run(s, new SearchQuery { Kind = SearchKind.FoldersOwnContent })));
        Assert.Equal(["child", "other"], Names(s, Search.Run(s, new SearchQuery { Kind = SearchKind.FoldersOwnContent, MinSize = 100_000 })));
    }

    [Fact]
    public void FiltersByAgeAndSkipsDeleted()
    {
        using var t = new TestTree();
        string old = t.File("old.bin", 1000);
        t.File("new.bin", 1000);
        string gone = t.File("gone.bin", 1000);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-3));
        var s = Scan(t.Root);
        s.Remove(s.FindPath(gone));

        Assert.Equal(["old.bin"], Names(s, Search.Run(s, new SearchQuery { ModifiedBefore = DateTime.UtcNow.AddYears(-1) })));
        Assert.Equal(["new.bin"], Names(s, Search.Run(s, new SearchQuery { ModifiedAfter = DateTime.UtcNow.AddDays(-1) })));
    }

    [Fact]
    public void SearchesLargeTreeFast()
    {
        var s = Scan(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var r = Search.Run(s, new SearchQuery { Name = "*.dll" });
        output.WriteLine($"{s.Count:N0} nodes, {r.TotalMatches:N0} dlls, {Format.Size(r.TotalSize)}, {r.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(1000, r.Nodes.Count);
        Assert.True(s[r.Nodes[0]].Size >= s[r.Nodes[^1]].Size);
    }
}
