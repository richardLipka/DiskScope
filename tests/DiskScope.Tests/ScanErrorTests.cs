using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Duplicates;
using DiskScope.Core.Orphans;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;
using Xunit.Abstractions;

namespace DiskScope.Tests;

/// <summary>Scanning and analyses when the disk misbehaves: no crash, no hang, and no failure presented as success.</summary>
public class ScanErrorTests(ITestOutputHelper output)
{
    static ScanSession Scan(string path)
    {
        var session = new ScanSession(path, ScanMode.Directory);
        session.Start();
        Assert.True(Task.Run(session.Wait).Wait(TimeSpan.FromMinutes(2)), "scan did not finish");
        return session;
    }

    static char? FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'M'; c--)
            if (!used.Contains(c)) return c;
        return null;
    }

    static void Run(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    [Fact]
    public void UnreadableRootFailsInsteadOfLookingDone()
    {
        string missing = Path.Combine(Path.GetTempPath(), "DiskScopeMissing_" + Guid.NewGuid().ToString("N"));
        var session = Scan(missing);
        Assert.Equal(ScanState.Failed, session.State);
        Assert.Equal(ScanSourceProblem.RootUnreadable, Assert.IsType<ScanSourceException>(session.Error).Problem);
    }

    [Fact]
    public void DriveRemovedDuringScanDoesNotCrashOrLookComplete()
    {
        if (FreeDriveLetter() is not char letter) return;
        using var t = new TestTree();
        // Enough folders that the scan is still running when the drive goes away.
        for (int i = 0; i < 6000; i++) t.Dir($@"d{i % 300}\s{i}");
        Run("subst.exe", $"{letter}: \"{t.Root}\"");
        ScanSession session;
        try
        {
            session = new ScanSession($@"{letter}:\", ScanMode.Directory);
            session.Start();
        }
        finally
        {
            Run("subst.exe", $"{letter}: /D"); // pulled out while scanning
        }
        Assert.True(Task.Run(session.Wait).Wait(TimeSpan.FromMinutes(2)), "scan did not finish");
        output.WriteLine($"state {session.State}, errors {session.Progress.Errors}, files {session.Progress.Files}");
        // Either the scan was quicker than the removal (then complete and clean) or it must say it failed.
        if (session.State == ScanState.Completed) Assert.Equal(0, session.Progress.Errors);
        else Assert.IsType<ScanSourceException>(session.Error);
    }

    [Fact]
    public void AccessDeniedFolderIsReportedAndTheRestScanned()
    {
        using var t = new TestTree();
        t.File(@"open\a.bin", 1000);
        string locked = t.Dir("locked");
        t.File(@"locked\secret.bin", 1000);
        Run("icacls.exe", $"\"{locked}\" /deny \"{Environment.UserName}\":(RX)");
        try
        {
            var session = Scan(t.Root);
            Assert.Equal(ScanState.Completed, session.State);
            Assert.True(session.Progress.Errors >= 1);
            var s = session.Store;
            Assert.True((s[s.FindPath(locked)].Flags & NodeFlags.Error) != 0);
            Assert.NotEqual(NodeStore.None, s.FindPath(Path.Combine(t.Root, @"open\a.bin")));
        }
        finally
        {
            Run("icacls.exe", $"\"{locked}\" /remove:d \"{Environment.UserName}\"");
        }
    }

    [Fact]
    public void RescanOfVanishedFolderKeepsTheTree()
    {
        using var t = new TestTree();
        t.File(@"gone\a.bin", 1000);
        t.File(@"gone\b.bin", 2000);
        var s = Scan(t.Root).Store;
        int gone = s.FindPath(Path.Combine(t.Root, "gone"));
        Directory.Delete(Path.Combine(t.Root, "gone"), true);

        Assert.Throws<ScanSourceException>(() => new DirectoryScanner().Rescan(s, gone, new ScanProgress(), CancellationToken.None));
        Assert.Equal(2, s[gone].FileCount); // nothing was cleared
        Assert.NotEqual(NodeStore.None, s.FindPath(Path.Combine(t.Root, @"gone\b.bin")));
    }

    [Fact]
    public void FailingFinishedHandlerDoesNotEndTheProcess()
    {
        using var t = new TestTree();
        t.File("a.bin", 10);
        var session = new ScanSession(t.Root, ScanMode.Directory);
        session.Finished += _ => throw new InvalidOperationException("broken handler");
        session.Start();
        session.Wait();
        Assert.Equal(ScanState.Completed, session.State); // we are still here: the exception did not escape the thread
    }

    [Fact]
    public void DamagedSnapshotsAreRejectedNotCrashing()
    {
        using var t = new TestTree();
        t.File(@"x\a.bin", 1000);
        t.File(@"x\b.bin", 1000);
        var store = Scan(t.Root).Store;
        var catalog = new SnapshotCatalog(t.Dir("snaps"));
        string good = catalog.NewFilePath(store.RootPath, DateTime.UtcNow);
        Snapshot.Save(store, good, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
        byte[] bytes = File.ReadAllBytes(good);

        var damaged = new Dictionary<string, byte[]>
        {
            ["truncated"] = bytes[..(bytes.Length * 2 / 3)],
            ["header only"] = bytes[..40],
            ["garbage body"] = [.. bytes[..60], .. Enumerable.Repeat((byte)0xA5, 500)],
            ["not a snapshot"] = "hello"u8.ToArray(),
            ["empty"] = [],
        };
        foreach (var (name, content) in damaged)
        {
            string file = Path.Combine(catalog.Directory, name.Replace(' ', '_') + Snapshot.Extension);
            File.WriteAllBytes(file, content);
            var ex = Record.Exception(() => Snapshot.Load(file, out _));
            Assert.True(ex is InvalidDataException or EndOfStreamException or IOException, $"{name}: {ex?.GetType().Name ?? "no error"}");
        }
        // The catalog reads only headers: it skips files whose header is broken and does not trip over any of them.
        // (The truncated file keeps an intact header, so it is listed; opening it then fails with a message.)
        var listed = catalog.List().Select(s => Path.GetFileName(s.FilePath)).Order().ToList();
        Assert.Equal([Path.GetFileName(good), "truncated" + Snapshot.Extension], listed);
    }

    [Fact]
    public void DuplicateSearchReportsUnreadableFiles()
    {
        using var t = new TestTree();
        var data = new byte[200_000];
        new Random(3).NextBytes(data);
        foreach (string name in new[] { "a.bin", "b.bin", "c.bin" }) File.WriteAllBytes(Path.Combine(t.Root, name), data);
        string deleted = Path.Combine(t.Root, "d.bin");
        File.WriteAllBytes(deleted, data);
        var store = Scan(t.Root).Store;
        File.Delete(deleted); // deleted since the scan: skipped, not an error

        var progress = new DuplicateProgress();
        List<DuplicateGroup> groups;
        using (new FileStream(Path.Combine(t.Root, "c.bin"), FileMode.Open, FileAccess.Read, FileShare.None)) // locked
            groups = new DuplicateFinder { MinSize = 1 }.Find(store, progress);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Nodes.Count);
        Assert.Equal(1, progress.Unreadable);
    }

    [Fact]
    public void DuplicateSearchOnVanishedDriveReportsInsteadOfFindingNothing()
    {
        if (FreeDriveLetter() is not char letter) return;
        using var t = new TestTree();
        var data = new byte[200_000];
        File.WriteAllBytes(Path.Combine(t.Root, "a.bin"), data);
        File.WriteAllBytes(Path.Combine(t.Root, "b.bin"), data);
        Run("subst.exe", $"{letter}: \"{t.Root}\"");
        NodeStore store;
        try
        {
            store = Scan($@"{letter}:\").Store;
        }
        finally
        {
            Run("subst.exe", $"{letter}: /D");
        }
        var progress = new DuplicateProgress();
        var groups = new DuplicateFinder { MinSize = 1 }.Find(store, progress);
        Assert.Empty(groups);
        Assert.Equal(2, progress.Unreadable); // "found nothing" would be a lie; it says the files could not be read
    }

    [Fact]
    public void OrphanFinderSkipsStoreAppsWhenTheirListIsUnavailable()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int Link(int parent, int child)
        {
            s[child].NextSibling = s[parent].FirstChild;
            s[parent].FirstChild = child;
            return child;
        }
        int Dir(int parent, string name) => Link(parent, s.Add(parent, name, NodeFlags.Directory, 0, 0, 0, 0));
        int packages = Dir(Dir(Dir(Dir(Dir(root, "Users"), "me"), "AppData"), "Local"), "Packages");
        int app = Dir(packages, "Some.App_abc");
        Link(app, s.Add(app, "data.bin", NodeFlags.None, 0, 100L << 20, 100L << 20, 0));
        s.ComputeTotalsFromRoot();

        var found = new OrphanFinder().Find(s, [new InstalledProgram("Other", null, null)], new HashSet<string>(), [], DateTime.UtcNow,
            p => p.Replace("%SystemDrive%", "C:"));
        Assert.Empty(found); // an empty Store list must not make every Store app look abandoned
    }
}
