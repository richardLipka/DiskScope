using DiskScope.Core;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;

namespace DiskScope.Tests;

public class AccessTimeTests
{
    static readonly DateTime OldWrite = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime OldAccess = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime NewWrite = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime NewAccess = new(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    static NodeStore Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        session.Wait();
        return session.Store;
    }

    static TestTree MakeTree(out string oldFile, out string newFile)
    {
        var t = new TestTree();
        oldFile = t.File(@"archive\deep\old.bin", 1000);
        newFile = t.File(@"work\new.bin", 1000);
        File.SetLastWriteTimeUtc(oldFile, OldWrite);
        File.SetLastAccessTimeUtc(oldFile, OldAccess);
        File.SetLastWriteTimeUtc(newFile, NewWrite);
        File.SetLastAccessTimeUtc(newFile, NewAccess);
        // Folder write times are set by the file creation above; push them into the past too.
        foreach (var dir in new[] { @"archive\deep", "archive", "work" })
            Directory.SetLastWriteTimeUtc(Path.Combine(t.Root, dir), OldWrite);
        return t;
    }

    [Fact]
    public void ScannerKeepsAccessTimesAndFoldersShowTheNewestInside()
    {
        using var t = MakeTree(out string oldFile, out string newFile);
        var s = Scan(t.Root);

        int old = s.FindPath(oldFile), fresh = s.FindPath(newFile);
        Assert.Equal(OldAccess.ToFileTimeUtc(), s[old].ATime);
        Assert.Equal(OldWrite.ToFileTimeUtc(), s[old].MTime);
        Assert.Equal(NewAccess.ToFileTimeUtc(), s[fresh].ATime);

        int archive = s.FindPath(Path.Combine(t.Root, "archive"));
        Assert.Equal(OldAccess.ToFileTimeUtc(), s[archive].ATime);
        Assert.Equal(OldWrite.ToFileTimeUtc(), s[archive].MTime);
        Assert.Equal(NewAccess.ToFileTimeUtc(), s[s.Root].ATime);
        Assert.True(s[s.Root].MTime >= NewWrite.ToFileTimeUtc());
    }

    [Fact]
    public void SearchFindsItemsNotOpenedForLong()
    {
        using var t = MakeTree(out _, out _);
        var s = Scan(t.Root);
        var cutoff = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var files = Search.Run(s, new SearchQuery { AccessedBefore = cutoff });
        Assert.Equal(["old.bin"], files.Nodes.Select(s.GetName));
        var folders = Search.Run(s, new SearchQuery { Kind = SearchKind.Folders, AccessedBefore = cutoff });
        Assert.Equal(["archive", "deep"], folders.Nodes.Select(s.GetName).Order());
        var unchanged = Search.Run(s, new SearchQuery { Kind = SearchKind.Folders, ModifiedBefore = cutoff });
        Assert.Equal(["archive", "deep"], unchanged.Nodes.Select(s.GetName).Order());
    }

    [Fact]
    public void SnapshotKeepsAccessTimesAndReadsTheOldFormat()
    {
        using var t = MakeTree(out string oldFile, out _);
        var s = Scan(t.Root);
        string v2 = Path.Combine(t.Root, "..", Guid.NewGuid() + Snapshot.Extension);
        string v1 = Path.Combine(t.Root, "..", Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Snapshot.Save(s, v2, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
            var loaded = Snapshot.Load(v2, out _);
            Assert.Equal(OldAccess.ToFileTimeUtc(), loaded[loaded.FindPath(oldFile)].ATime);
            Assert.Equal(s[s.FindPath(Path.Combine(t.Root, "archive"))].ATime, loaded[loaded.FindPath(Path.Combine(t.Root, "archive"))].ATime);

            Snapshot.Save(s, v1, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0, null, formatVersion: 1);
            var old = Snapshot.Load(v1, out _);
            Assert.Equal(0, old[old.FindPath(oldFile)].ATime);
            Assert.Equal(OldWrite.ToFileTimeUtc(), old[old.FindPath(oldFile)].MTime);
            Assert.Equal(s[s.Root].Size, old[old.Root].Size);
        }
        finally
        {
            File.Delete(v2);
            File.Delete(v1);
        }
    }

    [Fact]
    public void FolderSummaryListsLargestEntriesTypesAndFiles()
    {
        using var t = new TestTree();
        t.File(@"cache\a.tmp", 50_000);
        t.File(@"cache\b.tmp", 40_000);
        t.File(@"cache\sub\c.log", 30_000);
        t.File(@"cache\README", 100);
        var s = Scan(t.Root);
        int cache = s.FindPath(Path.Combine(t.Root, "cache"));

        var summary = FolderSummary.Build(s, cache);
        Assert.Equal(["a.tmp", "b.tmp", "sub", "README"], summary.LargestChildren.Select(s.GetName));
        Assert.Equal(".tmp", summary.Extensions[0].Extension);
        Assert.Equal(2, summary.Extensions[0].Count);
        Assert.Contains(summary.Extensions, e => e.Extension == "" && e.Count == 1);
        Assert.Equal(["a.tmp", "b.tmp", "c.log", "README"], summary.LargestFiles.Select(s.GetName));
        Assert.False(summary.Truncated);
    }
}
