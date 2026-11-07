using System.Diagnostics;
using DiskScope.Core.Deletion;

namespace DiskScope.Tests;

/// <summary>Deleting when the disk misbehaves: the app must report it, not crash and not claim success.</summary>
public class DiskErrorTests
{
    sealed class FailingDeleter : IFileDeleter
    {
        public readonly List<string> Calls = [];

        public DeleteResult Delete(string path, bool isDirectory, DeleteMethod method, CancellationToken cancel)
        {
            lock (Calls) Calls.Add(path);
            return path switch
            {
                "io" => throw new IOException("The request could not be performed because of an I/O device error."),
                "denied" => throw new UnauthorizedAccessException("Access is denied."),
                "bug" => throw new NullReferenceException("unexpected"),
                "notready" => new DeleteResult(false, 0, "The device is not ready."),
                _ => new DeleteResult(true, 0, null),
            };
        }
    }

    [Fact]
    public void QueueSurvivesDeleterExceptionsAndFailingSubscribers()
    {
        var deleter = new FailingDeleter();
        using var queue = new DeleteQueue(deleter);
        int notified = 0;
        queue.JobChanged += _ =>
        {
            Interlocked.Increment(ref notified);
            throw new InvalidOperationException("a broken event handler");
        };
        string[] paths = ["io", "denied", "bug", "notready", "ok"];
        queue.Enqueue(paths.Select(p => new DeleteJob(p, false, 1, DeleteMethod.Permanent)));
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));

        var jobs = queue.Jobs;
        Assert.Equal(paths, deleter.Calls); // every job reached the deleter, one after another
        Assert.Equal([DeleteState.Failed, DeleteState.Failed, DeleteState.Failed, DeleteState.Failed, DeleteState.Done],
            jobs.Select(j => j.State));
        Assert.Contains("I/O device error", jobs[0].Error);
        Assert.True(notified >= 10);

        // The worker is still alive after all of that.
        queue.Enqueue([new DeleteJob("later", false, 1, DeleteMethod.Permanent)]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(DeleteState.Done, queue.Jobs[^1].State);
    }

    static char? FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'M'; c--)
            if (!used.Contains(c)) return c;
        return null;
    }

    static void Subst(string args)
    {
        var p = Process.Start(new ProcessStartInfo("subst.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    [Theory]
    [InlineData(DeleteMethod.Permanent)]
    [InlineData(DeleteMethod.RecycleBin)]
    public void DriveThatDisappearedIsReportedNotTakenAsDeleted(DeleteMethod method)
    {
        if (FreeDriveLetter() is not char letter) return;
        using var t = new TestTree();
        t.File(@"data\file.bin", 1000);
        Subst($"{letter}: \"{t.Root}\"");
        string onDrive = $@"{letter}:\data\file.bin";
        try
        {
            Assert.True(File.Exists(onDrive));
        }
        finally
        {
            Subst($"{letter}: /D"); // the drive goes away, like an unplugged USB disk
        }

        var result = new FileDeleter().Delete(onDrive, false, method, CancellationToken.None);
        Assert.False(result.Gone);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.True(File.Exists(Path.Combine(t.Root, @"data\file.bin")));

        // Through the queue: reported as failed, so the tree keeps the item.
        using var queue = new DeleteQueue(new FileDeleter());
        queue.Enqueue([new DeleteJob($@"{letter}:\data", true, 1000, method)]);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal(DeleteState.Failed, queue.Jobs[0].State);
    }

    [Fact]
    public void ExtremelyDeepFolderIsDeletedWithoutStackOverflow()
    {
        using var t = new TestTree();
        // 1 000 nested folders (2 000 characters of path, far beyond MAX_PATH). Much deeper trees are possible, but
        // every path operation walks all levels in the kernel, so building one for a test would take minutes.
        string deepest = t.Root + string.Concat(Enumerable.Repeat(@"\a", 1_000));
        Directory.CreateDirectory(deepest);
        File.WriteAllText(Path.Combine(deepest, "bottom.txt"), "x");

        var result = new FileDeleter().Delete(Path.Combine(t.Root, "a"), true, DeleteMethod.Permanent, CancellationToken.None);
        Assert.True(result.Gone, result.Error);
        Assert.Equal(0, result.FailedEntries);
    }

    [Fact]
    public void MissingFileCountsAsGone()
    {
        using var t = new TestTree();
        var result = new FileDeleter().Delete(Path.Combine(t.Root, "never-existed.bin"), false, DeleteMethod.Permanent, CancellationToken.None);
        Assert.True(result.Gone);
    }
}
