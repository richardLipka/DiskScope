using DiskScope.Core;

namespace DiskScope.Tests;

public class FileTypesTests
{
    /// <summary>C:\t with Videos (two .mp4, one .MKV), Code (node_modules with .js files, one .cs) and loose files.</summary>
    static (NodeStore Store, Dictionary<string, int> Nodes) Build()
    {
        var s = new NodeStore();
        var nodes = new Dictionary<string, int>();
        int root = s.CreateRoot(@"C:\t");
        nodes[@"C:\t"] = root;
        var heads = new Dictionary<int, int>();
        int Add(string path, long size)
        {
            string parentPath = Path.GetDirectoryName(path)!;
            int parent = nodes[parentPath];
            bool dir = size < 0;
            int n = s.Add(parent, Path.GetFileName(path), dir ? NodeFlags.Directory | NodeFlags.Scanned : NodeFlags.None, 0,
                Math.Max(0, size), Math.Max(0, size), 1);
            s[n].NextSibling = heads.GetValueOrDefault(parent, NodeStore.None);
            heads[parent] = n;
            nodes[path] = n;
            return n;
        }
        Add(@"C:\t\Videos", -1);
        Add(@"C:\t\Videos\a.mp4", 700_000_000);
        Add(@"C:\t\Videos\b.mp4", 300_000_000);
        Add(@"C:\t\Videos\c.MKV", 2_000_000_000);
        Add(@"C:\t\Code", -1);
        Add(@"C:\t\Code\node_modules", -1);
        Add(@"C:\t\Code\node_modules\x.js", 4096);
        Add(@"C:\t\Code\node_modules\y.js", 8192);
        Add(@"C:\t\Code\node_modules\README", 4096);
        Add(@"C:\t\Code\app.cs", 12_288);
        Add(@"C:\t\notes.final version", 4096);
        Add(@"C:\t\.gitignore", 4096);
        foreach (var (parent, head) in heads) s.PublishChildren(parent, head);
        s.ComputeTotalsFromRoot();
        return (s, nodes);
    }

    [Theory]
    [InlineData("movie.MP4", ".mp4")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("README", "")]
    [InlineData(".gitignore", "")]
    [InlineData("trailing.", "")]
    [InlineData("notes.final version", "")]
    [InlineData("x.averyveryverylongextension", "")]
    public void ExtensionsAreRecognised(string name, string expected) => Assert.Equal(expected, FileTypes.ExtensionOf(name));

    [Fact]
    public void KindsComeFromTheExtension()
    {
        Assert.Equal(FileKind.Video, FileTypes.KindOf("a.MKV"));
        Assert.Equal(FileKind.DiskImage, FileTypes.KindOf("disk.vhdx"));
        Assert.Equal(FileKind.Other, FileTypes.KindOf("README"));
    }

    [Fact]
    public void TotalsPerExtensionLargestFirst()
    {
        var (s, _) = Build();
        var totals = FileTypes.Totals(s, s.Root);
        Assert.Equal(".mkv", totals[0].Extension);
        Assert.Equal(FileKind.Video, totals[0].Kind);
        var mp4 = totals.Single(t => t.Extension == ".mp4");
        Assert.Equal(2, mp4.Count);
        Assert.Equal(1_000_000_000, mp4.Size);
        var none = totals.Single(t => t.Extension == "");
        Assert.Equal(3, none.Count); // README, "notes.final version", .gitignore
        Assert.Equal(s[s.Root].Size, totals.Sum(t => t.Size));
        Assert.Equal(s[s.Root].FileCount, totals.Sum(t => t.Count));
    }

    [Fact]
    public void LargestFilesOfOneExtension()
    {
        var (s, n) = Build();
        Assert.Equal([n[@"C:\t\Videos\a.mp4"], n[@"C:\t\Videos\b.mp4"]], FileTypes.LargestFiles(s, s.Root, ".mp4", 10));
        Assert.Equal([n[@"C:\t\Videos\a.mp4"]], FileTypes.LargestFiles(s, s.Root, ".MP4", 1));
        Assert.Equal(3, FileTypes.LargestFiles(s, s.Root, "", 10).Count);
        Assert.Empty(FileTypes.LargestFiles(s, n[@"C:\t\Code"], ".mp4", 10));
    }

    [Fact]
    public void SizeFilterHidesSmallItems()
    {
        var (s, n) = Build();
        var f = new TreeFilter(null, 100_000_000);
        f.Prepare(s);
        Assert.True(f.Shows(s, n[@"C:\t\Videos"]));
        Assert.True(f.Shows(s, n[@"C:\t\Videos\b.mp4"]));
        Assert.False(f.Shows(s, n[@"C:\t\Code"]));
        Assert.False(f.Shows(s, n[@"C:\t\.gitignore"]));
    }

    [Fact]
    public void NameFilterShowsMatchesAndTheWayToThem()
    {
        var (s, n) = Build();
        var f = new TreeFilter("*.js", 0);
        f.Prepare(s);
        Assert.True(f.Shows(s, n[@"C:\t\Code"]));
        Assert.True(f.Shows(s, n[@"C:\t\Code\node_modules"]));
        Assert.True(f.Shows(s, n[@"C:\t\Code\node_modules\x.js"]));
        Assert.False(f.Shows(s, n[@"C:\t\Code\node_modules\README"]));
        Assert.False(f.Shows(s, n[@"C:\t\Code\app.cs"]));
        Assert.False(f.Shows(s, n[@"C:\t\Videos"]));
    }

    [Fact]
    public void AMatchingFolderShowsEverythingInside()
    {
        var (s, n) = Build();
        var f = new TreeFilter("node_modules", 0);
        f.Prepare(s);
        Assert.True(f.Shows(s, n[@"C:\t\Code"]));
        Assert.True(f.Shows(s, n[@"C:\t\Code\node_modules"]));
        Assert.True(f.Shows(s, n[@"C:\t\Code\node_modules\README"]));
        Assert.False(f.Shows(s, n[@"C:\t\Code\app.cs"]));
    }

    [Fact]
    public void SeveralPatternsAndSizeTogether()
    {
        var (s, n) = Build();
        var f = new TreeFilter("*.mp4; *.cs", 500_000_000);
        f.Prepare(s);
        Assert.True(f.Shows(s, n[@"C:\t\Videos\a.mp4"]));
        Assert.False(f.Shows(s, n[@"C:\t\Videos\b.mp4"]));  // too small
        Assert.False(f.Shows(s, n[@"C:\t\Videos\c.MKV"]));  // not a match
        Assert.False(f.Shows(s, n[@"C:\t\Code"]));          // the .cs file is too small, and so is the folder
    }

    [Fact]
    public void TreeRowsUseTheFilter()
    {
        var (s, n) = Build();
        var f = new TreeFilter("*.mp4", 0);
        f.Prepare(s);
        var rows = new TreeRows(s) { Filter = x => f.Shows(s, x) };
        rows.SetExpanded(s.Root, true);
        rows.SetExpanded(n[@"C:\t\Videos"], true);
        rows.Rebuild();
        var shown = Enumerable.Range(0, rows.Count).Select(i => s.GetName(rows.NodeAt(i))).ToList();
        Assert.Equal([@"C:\t", "Videos", "a.mp4", "b.mp4"], shown);
    }
}
