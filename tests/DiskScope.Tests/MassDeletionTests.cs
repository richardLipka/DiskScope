using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Deletion;
using Xunit.Abstractions;

namespace DiskScope.Tests;

/// <summary>Emptying a folder of 100 000 files must take linear time everywhere: queue, tree and checks.</summary>
public class MassDeletionTests(ITestOutputHelper output)
{
    const int Many = 100_000;

    sealed class InstantDeleter : IFileDeleter
    {
        public int Calls;
        public DeleteResult Delete(string path, bool isDirectory, DeleteMethod method, CancellationToken cancel)
        {
            Interlocked.Increment(ref Calls);
            return new DeleteResult(true, 0, null);
        }
    }

    [Fact]
    public void QueueHandlesHundredThousandJobsQuickly()
    {
        var deleter = new InstantDeleter();
        using var queue = new DeleteQueue(deleter);
        int notified = 0;
        queue.JobChanged += _ => Interlocked.Increment(ref notified);
        var clock = Stopwatch.StartNew();
        // Paths under the temp folder: allowed by the guard, and the parent's real path is looked up once.
        string folder = Path.Combine(Path.GetTempPath(), "DiskScopeMass");
        queue.Enqueue(Enumerable.Range(0, Many).Select(i => new DeleteJob(Path.Combine(folder, $"f{i}.tmp"), false, 1, DeleteMethod.Permanent)));
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(60)), "queue did not finish");
        output.WriteLine($"{Many:N0} jobs in {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(Many, deleter.Calls);
        Assert.Equal(0, queue.WaitingCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.True(notified >= 2 * Many - 1);
    }

    [Fact]
    public void CancelTakesBackOnlyWaitingJobsAndKeepsTheCount()
    {
        using var queue = new DeleteQueue(new InstantDeleter()) { Paused = true };
        queue.Enqueue(Enumerable.Range(0, 1000).Select(i => new DeleteJob($@"C:\tmp\x{i}", false, 1, DeleteMethod.Permanent)));
        Assert.Equal(1000, queue.WaitingCount);
        Assert.Equal(1000, queue.CancelWaiting().Count);
        Assert.Equal(0, queue.WaitingCount);
        Assert.All(queue.Jobs, j => Assert.Equal(DeleteState.Cancelled, j.State));
    }

    [Fact]
    public void RemoveManyIsLinearAndKeepsTotalsRight()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int temp = s.Add(root, "Temp", NodeFlags.Directory, 0, 0, 0, 0);
        int keep = s.Add(root, "keep.bin", NodeFlags.None, 0, 7, 7, 0);
        s[temp].NextSibling = keep;
        s.PublishChildren(root, temp);
        int head = NodeStore.None;
        var files = new List<int>();
        for (int i = 0; i < Many; i++)
        {
            int f = s.Add(temp, "f" + i, NodeFlags.None, 0, 10, 10, 0);
            s[f].NextSibling = head;
            head = f;
            files.Add(f);
        }
        s.PublishChildren(temp, head);
        s.ComputeTotalsFromRoot();

        var clock = Stopwatch.StartNew();
        s.RemoveMany(files.Where((_, i) => i % 10 != 0)); // all but every tenth
        output.WriteLine($"removed {Many * 9 / 10:N0} in {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));

        Assert.Equal(Many / 10, s[temp].FileCount);
        Assert.Equal(Many / 10 * 10L, s[temp].Size);
        Assert.Equal(Many / 10 * 10L + 7, s[root].Size);
        int listed = 0;
        for (int c = s[temp].FirstChild; c != NodeStore.None; c = s[c].NextSibling) listed++;
        Assert.Equal(Many / 10, listed);

        // Nested and repeated nodes are handled: removing the folder and some of its files counts each once.
        s.RemoveMany([temp, files[0], files[0], keep]);
        Assert.Equal(0, s[root].Size);
        Assert.Equal(0, s[root].FileCount);
        Assert.Equal(0, s[root].DirCount);
        Assert.Equal(NodeStore.None, s[root].FirstChild);
    }

    [Fact]
    public void GuardWithCacheGivesTheSameAnswersFaster()
    {
        var guard = new DeletionGuard();
        string folder = Path.GetTempPath();
        var paths = Enumerable.Range(0, 20_000).Select(i => Path.Combine(folder, $"x{i}.tmp")).ToList();
        paths.Add(@"C:\Windows\System32\kernel32.dll");
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var clock = Stopwatch.StartNew();
        var cached = paths.Select(p => guard.CheckResolved(p, cache)).ToList();
        var withCache = clock.Elapsed;
        output.WriteLine($"{paths.Count:N0} checks with cache: {withCache.TotalMilliseconds:0} ms, {cache.Count} lookups");
        Assert.True(cache.Count <= 2);
        Assert.Equal(DeletionGuard.Verdict.InsideWindows, cached[^1]);
        Assert.All(cached.Take(paths.Count - 1), v => Assert.Equal(DeletionGuard.Verdict.Allowed, v));
        Assert.Equal(cached.Take(50), paths.Take(50).Select(guard.CheckResolved));
    }
}
