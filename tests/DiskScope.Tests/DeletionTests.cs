using System.Diagnostics;
using DiskScope.Core.Deletion;

namespace DiskScope.Tests;

public class DeletionTests
{
    static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    [Fact]
    public void PermanentDeleteRemovesTreeButNotJunctionTargets()
    {
        using var t = new TestTree();
        string keep = t.File(@"outside\precious.txt", 100);
        string ro = t.File(@"victim\sub\readonly.txt", 100);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        t.File(@"victim\a.bin", 1000);
        Junction(Path.Combine(t.Root, @"victim\link"), Path.Combine(t.Root, "outside"));

        var result = new FileDeleter().Delete(Path.Combine(t.Root, "victim"), true, DeleteMethod.Permanent, CancellationToken.None);

        Assert.True(result.Gone);
        Assert.Equal(0, result.FailedEntries);
        Assert.True(File.Exists(keep)); // the junction was removed, not followed
    }

    [Fact]
    public void PermanentDeleteSkipsLockedFilesAndDeletesTheRest()
    {
        using var t = new TestTree();
        string locked = t.File(@"dir\locked.bin", 100);
        string other = t.File(@"dir\other.bin", 100);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new FileDeleter().Delete(Path.Combine(t.Root, "dir"), true, DeleteMethod.Permanent, CancellationToken.None);
            Assert.False(result.Gone);
            Assert.True(result.FailedEntries >= 1);
            Assert.NotNull(result.Error);
        }
        Assert.False(File.Exists(other));
        Assert.True(File.Exists(locked));
    }

    sealed class SlowFakeDeleter : IFileDeleter
    {
        public readonly List<string> Order = [];
        public int Concurrent, MaxConcurrent;

        public DeleteResult Delete(string path, bool isDirectory, DeleteMethod method, CancellationToken cancel)
        {
            int now = Interlocked.Increment(ref Concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            Thread.Sleep(30);
            lock (Order) Order.Add(path);
            Interlocked.Decrement(ref Concurrent);
            return path.Contains("fail") ? new DeleteResult(false, 1, "locked") : new DeleteResult(true, 0, null);
        }
    }

    [Fact]
    public void QueueDeletesOneByOneInOrder()
    {
        var fake = new SlowFakeDeleter();
        using var queue = new DeleteQueue(fake);
        var changes = new List<(string, DeleteState)>();
        queue.JobChanged += j => { lock (changes) changes.Add((j.Path, j.State)); };

        queue.Enqueue([new DeleteJob("a", false, 1, DeleteMethod.Permanent), new DeleteJob("fail", false, 1, DeleteMethod.Permanent)]);
        queue.Enqueue([new DeleteJob("c", false, 1, DeleteMethod.RecycleBin)]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));

        Assert.Equal(["a", "fail", "c"], fake.Order);
        Assert.Equal(1, fake.MaxConcurrent);
        var jobs = queue.Jobs;
        Assert.Equal([DeleteState.Done, DeleteState.Failed, DeleteState.Done], jobs.Select(j => j.State));
        Assert.Equal("locked", jobs[1].Error);
        Assert.Contains(("a", DeleteState.Running), changes);

        queue.ClearFinished();
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public void QueueRefusesProtectedPlacesAndFilesChangedSinceTheScan()
    {
        using var t = new TestTree();
        string changed = t.File("changed.bin", 100);
        string same = t.File("same.bin", 100);
        var scannedTime = File.GetLastWriteTimeUtc(same).ToFileTimeUtc();
        File.WriteAllBytes(changed, new byte[150]); // grown after the scan

        var fake = new SlowFakeDeleter();
        using var queue = new DeleteQueue(fake);
        queue.Enqueue([
            new DeleteJob(@"C:\Windows\System32", true, 1, DeleteMethod.Permanent),
            new DeleteJob(changed, false, 100, DeleteMethod.Permanent) { ExpectedLength = 100, ExpectedMTime = scannedTime },
            new DeleteJob(same, false, 100, DeleteMethod.Permanent) { ExpectedLength = 100, ExpectedMTime = scannedTime },
        ]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));

        var jobs = queue.Jobs;
        Assert.Equal(DeleteJob.ErrorProtected, jobs[0].Error);
        Assert.Equal(DeleteJob.ErrorChanged, jobs[1].Error);
        Assert.Equal(DeleteState.Done, jobs[2].State);
        Assert.Equal([same], fake.Order); // the deleter was never called for the other two
    }

    [Fact]
    public void PausedQueueKeepsItemsUntilResumedOrCancelled()
    {
        var fake = new SlowFakeDeleter();
        using var queue = new DeleteQueue(fake) { Paused = true };
        queue.Enqueue([new DeleteJob("x", false, 1, DeleteMethod.Permanent), new DeleteJob("y", false, 1, DeleteMethod.Permanent)]);
        Thread.Sleep(150);
        Assert.Empty(fake.Order);

        var cancelled = queue.CancelWaiting();
        Assert.Equal(2, cancelled.Count);
        queue.Enqueue([new DeleteJob("z", false, 1, DeleteMethod.Permanent)]);
        queue.Paused = false;
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(["z"], fake.Order);
    }
}
