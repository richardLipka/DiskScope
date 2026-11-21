using DiskScope.Core;
using DiskScope.Core.Deletion;
using DiskScope.Core.Native;

namespace DiskScope.Tests;

/// <summary>Freeing space without deleting: moving (with and without a junction), compressing, virtual disks.</summary>
public class FreeSpaceTests
{
    static void Text(string path, int kilobytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Repetitive text compresses very well.
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("DiskScope compresses this line again and again. ", kilobytes * 1024 / 48)));
    }

    [Fact]
    public void MovingAFolderOnTheSameDriveRenamesIt()
    {
        using var t = new TestTree();
        t.File(@"src\project\a.bin", 5000);
        t.File(@"src\project\sub\b.bin", 7000);
        string target = t.Dir("dest");
        var result = FileMover.Move(Path.Combine(t.Root, @"src\project"), true, target, leaveLink: false, null, CancellationToken.None);
        Assert.True(result.Moved, result.Error);
        Assert.Equal(0, result.Freed); // same drive: nothing freed
        Assert.False(Directory.Exists(Path.Combine(t.Root, @"src\project")));
        Assert.Equal(7000, new FileInfo(Path.Combine(target, @"project\sub\b.bin")).Length);
    }

    [Fact]
    public void CopyingMoveKeepsContentTimesAndDeletesTheOriginal()
    {
        using var t = new TestTree();
        string a = t.File(@"src\project\a.bin", 5000);
        t.File(@"src\project\sub\b.bin", 7000);
        t.Dir(@"src\project\empty");
        var old = new DateTime(2020, 5, 17, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, old);
        string target = t.Dir("dest");
        long progress = 0;
        var result = FileMover.Move(Path.Combine(t.Root, @"src\project"), true, target, leaveLink: false, p => progress = p, CancellationToken.None,
            forceCopy: true);
        Assert.True(result.Moved, result.Error);
        Assert.Equal(12000, result.Freed);
        Assert.Equal(12000, progress);
        Assert.False(Directory.Exists(Path.Combine(t.Root, @"src\project")));
        Assert.Equal(old, File.GetLastWriteTimeUtc(Path.Combine(target, @"project\a.bin")));
        Assert.True(Directory.Exists(Path.Combine(target, @"project\empty")));
    }

    [Fact]
    public void MovingLeavesAWorkingJunction()
    {
        using var t = new TestTree();
        t.File(@"app\data\big.bin", 9000);
        string target = t.Dir("elsewhere");
        string original = Path.Combine(t.Root, @"app\data");
        var result = FileMover.Move(original, true, target, leaveLink: true, null, CancellationToken.None, forceCopy: true);
        Assert.True(result.Moved, result.Error);
        // The old path still works and leads to the new place.
        Assert.True((new DirectoryInfo(original).Attributes & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(9000, new FileInfo(Path.Combine(original, "big.bin")).Length);
        Assert.Equal(Path.Combine(target, "data"), Junction.Target(original), ignoreCase: true);
        // Deleting the junction removes only the link.
        Directory.Delete(original);
        Assert.True(File.Exists(Path.Combine(target, @"data\big.bin")));
    }

    [Fact]
    public void AnExistingTargetOrAMoveIntoItselfIsRefused()
    {
        using var t = new TestTree();
        t.File(@"src\x\a.bin", 100);
        t.File(@"dest\x\other.bin", 100);
        var exists = FileMover.Move(Path.Combine(t.Root, @"src\x"), true, Path.Combine(t.Root, "dest"), false, null, CancellationToken.None);
        Assert.Equal(FileMover.ErrorExists, exists.Error);
        var into = FileMover.Move(Path.Combine(t.Root, "src"), true, Path.Combine(t.Root, @"src\x"), false, null, CancellationToken.None);
        Assert.Equal(FileMover.ErrorIntoItself, into.Error);
        Assert.True(File.Exists(Path.Combine(t.Root, @"src\x\a.bin")));
    }

    [Fact]
    public void AFolderWithLinksInsideIsNotCopied()
    {
        using var t = new TestTree();
        t.File(@"src\proj\a.bin", 100);
        string elsewhere = t.Dir("elsewhere");
        Junction.Create(Path.Combine(t.Root, @"src\proj\link"), elsewhere);
        var result = FileMover.Move(Path.Combine(t.Root, @"src\proj"), true, t.Dir("dest"), false, null, CancellationToken.None, forceCopy: true);
        Assert.False(result.Moved);
        Assert.Equal(FileMover.ErrorContainsLinks, result.Error);
        Assert.True(File.Exists(Path.Combine(t.Root, @"src\proj\a.bin")));   // original untouched
        Assert.False(Directory.Exists(Path.Combine(t.Root, @"dest\proj")));   // no partial copy left
    }

    [Fact]
    public void CancellingACopyLeavesTheOriginalAndNoPartialCopy()
    {
        using var t = new TestTree();
        for (int i = 0; i < 20; i++) t.File($@"src\proj\f{i}.bin", 50_000);
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => FileMover.Move(Path.Combine(t.Root, @"src\proj"), true, t.Dir("dest"), false,
            p => { if (p > 200_000) cancel.Cancel(); }, cancel.Token, forceCopy: true));
        Assert.Equal(20, Directory.GetFiles(Path.Combine(t.Root, @"src\proj")).Length);
        Assert.False(Directory.Exists(Path.Combine(t.Root, @"dest\proj")));
    }

    [Fact]
    public void CompressingShrinksCompressibleFiles()
    {
        using var t = new TestTree();
        Text(Path.Combine(t.Root, @"old\notes1.txt"), 512);
        Text(Path.Combine(t.Root, @"old\notes2.txt"), 256);
        var result = FileCompressor.Compress(Path.Combine(t.Root, "old"), true, CompressionKind.Xpress8K, null, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Files);
        Assert.True(result.After < result.Before / 4, $"{result.Before} -> {result.After}");
        // The files read back unchanged.
        Assert.StartsWith("DiskScope compresses this line", File.ReadAllText(Path.Combine(t.Root, @"old\notes1.txt")));
        Assert.True(FileCompressor.OnDiskSize(Path.Combine(t.Root, @"old\notes1.txt")) < 512 * 1024 / 4);
    }

    [Fact]
    public void AScanCountsCompressedFilesWithTheirRealSize()
    {
        // Compressed files list an allocation of 0 in a directory listing; the scan must ask for the real size.
        using var t = new TestTree();
        for (int i = 0; i < 5; i++) Text(Path.Combine(t.Root, $@"data\f{i}.txt"), 256);
        var result = FileCompressor.Compress(Path.Combine(t.Root, "data"), true, CompressionKind.Xpress4K, null, CancellationToken.None);
        Assert.True(result.After > 0 && result.After < result.Before);

        var session = new DiskScope.Core.Scanning.ScanSession(t.Root, DiskScope.Core.Scanning.ScanMode.Directory);
        session.Start();
        session.Wait();
        long scanned = session.Store[session.Store.FindPath(Path.Combine(t.Root, "data"))].Size;
        Assert.Equal(result.After, scanned);
    }

    [Fact]
    public void IncompressibleAndTinyFilesAreNotFailures()
    {
        using var t = new TestTree();
        var random = new byte[300_000];
        new Random(1).NextBytes(random);
        File.WriteAllBytes(Path.Combine(t.Root, "random.bin"), random);
        File.WriteAllText(Path.Combine(t.Root, "tiny.txt"), "x");
        var result = FileCompressor.Compress(t.Root, true, CompressionKind.Lzx, null, CancellationToken.None);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Files);
    }

    [Theory]
    [InlineData(@"C:\", DeletionGuard.Verdict.DriveRoot)]
    [InlineData(@"C:\Windows", DeletionGuard.Verdict.InsideWindows)]
    [InlineData(@"C:\Windows\System32\drivers", DeletionGuard.Verdict.InsideWindows)]
    [InlineData(@"C:\Program Files\SomeGame", DeletionGuard.Verdict.Allowed)]
    [InlineData(@"C:\Program Files", DeletionGuard.Verdict.Allowed)]
    [InlineData(@"D:\Old projects", DeletionGuard.Verdict.Allowed)]
    public void CompressingIsRefusedForRootsAndWindows(string path, DeletionGuard.Verdict expected)
    {
        var guard = new DeletionGuard(s => s.Replace("%SystemRoot%", @"C:\Windows").Replace("%SystemDrive%", "C:"));
        Assert.Equal(expected, guard.CheckCompress(path));
    }

    [Fact]
    public void AJunctionToWindowsCannotBeCompressed()
    {
        using var t = new TestTree();
        string link = Path.Combine(t.Root, "innocent");
        Junction.Create(link, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Assert.Equal(DeletionGuard.Verdict.InsideWindows, new DeletionGuard().CheckCompress(link));
        Directory.Delete(link); // only the link
    }

    [Fact]
    public void TheQueueMovesAndCompresses()
    {
        using var t = new TestTree();
        Text(Path.Combine(t.Root, @"a\log.txt"), 256);
        t.File(@"b\data.bin", 4000);
        using var queue = new DeleteQueue(new FileDeleter());
        var compress = new DeleteJob(Path.Combine(t.Root, "a"), true, 256 * 1024, DeleteMethod.Compress) { Compression = CompressionKind.Xpress4K };
        var move = new DeleteJob(Path.Combine(t.Root, "b"), true, 4096, DeleteMethod.Move) { Destination = t.Dir("moved") };
        queue.Enqueue([compress, move]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal(DeleteState.Done, compress.State);
        Assert.True(compress.Freed > 100_000, compress.Freed.ToString());
        Assert.Equal(DeleteState.Done, move.State);
        Assert.True(File.Exists(Path.Combine(t.Root, @"moved\b\data.bin")));
    }

    [Fact]
    public void TheQueueRefusesToCompressWindows()
    {
        using var queue = new DeleteQueue(new FileDeleter());
        var job = new DeleteJob(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), true, 0, DeleteMethod.Compress);
        queue.Enqueue([job]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(DeleteState.Failed, job.State);
        Assert.Equal(DeleteJob.ErrorCompressRefused, job.Error);
    }

    [Fact]
    public void VirtualDiskScriptAttachesReadOnly()
    {
        string script = VirtualDisk.Script(@"C:\Users\x\AppData\Local\Docker\wsl\disk\docker_data.vhdx");
        Assert.Contains("attach vdisk readonly", script);
        Assert.Contains("compact vdisk", script);
        Assert.Contains("detach vdisk", script);
        Assert.Throws<ArgumentException>(() => VirtualDisk.Script("C:\\a\"\r\ndelete partition"));
        Assert.True(VirtualDisk.LooksLikeWsl(@"C:\Users\x\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu\LocalState\ext4.vhdx"));
        Assert.True(VirtualDisk.IsVirtualDisk("disk.VHDX"));
        Assert.False(VirtualDisk.IsVirtualDisk("disk.iso"));
    }
}
