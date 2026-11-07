using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Scanning;
using Xunit.Abstractions;

namespace DiskScope.Tests;

public class DirectoryScannerTests(ITestOutputHelper output)
{
    static ScanSession ScanNow(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        Assert.Equal(ScanState.Completed, session.State);
        return session;
    }

    [Fact]
    public void AggregatesSizesAndCounts()
    {
        using var t = new TestTree();
        t.File("a.bin", 10_000);
        t.File(@"sub\b.bin", 50_000);
        t.File(@"sub\deep\c.bin", 100_000);
        t.File(@"sub\deep\Žluťoučký kůň.txt", 5);
        t.Dir("empty");

        var s = ScanNow(t.Root).Store;
        ref var root = ref s[s.Root];
        Assert.Equal(4, root.FileCount);
        Assert.Equal(3, root.DirCount);
        Assert.Equal(160_005, root.Length);
        Assert.True(root.Size >= 160_005);

        int sub = s.FindPath(Path.Combine(t.Root, "sub"));
        Assert.NotEqual(NodeStore.None, sub);
        Assert.Equal(150_005, s[sub].Length);
        Assert.Equal(3, s[sub].FileCount);
        Assert.Equal(1, s[sub].DirCount);

        int kun = s.FindPath(Path.Combine(t.Root, @"sub\deep\Žluťoučký kůň.txt"));
        Assert.NotEqual(NodeStore.None, kun);
        Assert.Equal(5, s[kun].Length);
        Assert.True(s[s.FindPath(Path.Combine(t.Root, "empty"))].IsDirectory);
    }

    [Fact]
    public void DoesNotFollowJunctions()
    {
        using var t = new TestTree();
        t.File(@"target\big.bin", 100_000);
        string link = Path.Combine(t.Root, "link");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(t.Root, "target")}\"")
        { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link));

        var s = ScanNow(t.Root).Store;
        Assert.Equal(1, s[s.Root].FileCount);
        int l = s.FindPath(link);
        Assert.True((s[l].Flags & NodeFlags.Reparse) != 0);
        Assert.Equal(0, s[l].Size);
    }

    [Fact]
    public void ScansSystemFolderWithoutCrashing()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var session = ScanNow(Path.Combine(win, "System32"));
        var s = session.Store;
        output.WriteLine($"{s[s.Root].FileCount:N0} files, {s[s.Root].DirCount:N0} dirs, {Format.Size(s[s.Root].Size)}, " +
                         $"errors {session.Progress.Errors}, {session.Elapsed.TotalSeconds:0.0}s");
        Assert.True(s[s.Root].FileCount > 1000);
        Assert.Equal(session.Progress.Files, s[s.Root].FileCount);
    }

    [Fact]
    public void TreeRowsSortAndExpand()
    {
        using var t = new TestTree();
        t.File(@"small\x.bin", 1_000);
        t.File(@"big\y.bin", 500_000);
        t.File("z.bin", 100_000);

        var s = ScanNow(t.Root).Store;
        var rows = new TreeRows(s);
        rows.Rebuild();
        Assert.Equal(4, rows.Count);
        Assert.Equal("big", s.GetName(rows.NodeAt(1)));
        Assert.Equal("z.bin", s.GetName(rows.NodeAt(2)));
        Assert.Equal("small", s.GetName(rows.NodeAt(3)));

        rows.SetExpanded(rows.NodeAt(1), true);
        rows.Rebuild();
        Assert.Equal(5, rows.Count);
        Assert.Equal("y.bin", s.GetName(rows.NodeAt(2)));
        Assert.Equal(2, rows.DepthAt(2));

        rows.Sort = SortColumn.Name;
        rows.Descending = false;
        rows.Rebuild();
        Assert.Equal("big", s.GetName(rows.NodeAt(1)));
        Assert.Equal("small", s.GetName(rows.NodeAt(3)));
        Assert.Equal("z.bin", s.GetName(rows.NodeAt(4)));
    }
}
