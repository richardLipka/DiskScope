using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Duplicates;
using DiskScope.Core.Scanning;

namespace DiskScope.Tests;

public class DuplicateTests
{
    static byte[] Content(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    static string Write(TestTree t, string relative, byte[] data)
    {
        string path = Path.Combine(t.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    static NodeStore Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session.Store;
    }

    [Fact]
    public void FindsIdenticalFilesAndIgnoresLookalikesAndHardLinks()
    {
        using var t = new TestTree();
        var big = Content(3_000_000, 1);
        Write(t, @"a\movie.mkv", big);
        Write(t, @"b\copy of movie.mkv", big);
        Write(t, @"c\movie (2).mkv", big);

        // Same size, same first and last 64 KB, different in the middle: only the full hash can tell.
        var almost = (byte[])big.Clone();
        almost[1_500_000] ^= 0xFF;
        Write(t, @"d\almost.mkv", almost);

        var small = Content(200_000, 2);
        Write(t, @"e\doc.pdf", small);
        Write(t, @"f\doc.pdf", small);
        Write(t, "tiny1.txt", Content(500, 3));
        Write(t, "tiny2.txt", Content(500, 3));

        // A hard link is the same file, not a copy.
        string original = Write(t, @"g\original.iso", Content(1_200_000, 4));
        Directory.CreateDirectory(Path.Combine(t.Root, "h"));
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{Path.Combine(t.Root, @"h\link.iso")}\" \"{original}\"")
        { CreateNoWindow = true, UseShellExecute = false });
        p!.WaitForExit();
        Assert.True(File.Exists(Path.Combine(t.Root, @"h\link.iso")));

        var store = Scan(t.Root);
        var progress = new DuplicateProgress();
        var groups = new DuplicateFinder { MinSize = 100_000 }.Find(store, progress);

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Nodes.Count);
        Assert.Equal(["copy of movie.mkv", "movie (2).mkv", "movie.mkv"], groups[0].Nodes.Select(store.GetName).Order());
        Assert.Equal(2 * store[groups[0].Nodes[0]].Size, groups[0].Wasted);
        Assert.Equal(["doc.pdf", "doc.pdf"], groups[1].Nodes.Select(store.GetName));
        Assert.Equal(DuplicateStage.Done, progress.Stage);
        Assert.True(progress.BytesDone >= 3 * 3_000_000L);

        var everything = new DuplicateFinder { MinSize = 1 }.Find(store, new DuplicateProgress());
        Assert.Equal(3, everything.Count); // the tiny text files too, still not the hard link
    }

    [Fact]
    public void RespectsScope()
    {
        using var t = new TestTree();
        var data = Content(150_000, 5);
        Write(t, @"in\x.bin", data);
        Write(t, @"in\sub\y.bin", data);
        Write(t, @"out\z.bin", data);
        var store = Scan(t.Root);
        int scope = store.FindPath(Path.Combine(t.Root, "in"));
        var groups = new DuplicateFinder { MinSize = 1, Scope = scope }.Find(store, new DuplicateProgress());
        Assert.Single(groups);
        Assert.Equal(["x.bin", "y.bin"], groups[0].Nodes.Select(store.GetName).Order());
    }
}
