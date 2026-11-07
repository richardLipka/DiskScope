using System.Collections.Concurrent;
using DiskScope.Core;
using DiskScope.Core.Cleanup;
using DiskScope.Core.Orphans;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;
using Xunit.Abstractions;

namespace DiskScope.Tests;

/// <summary>
/// The app reads the tree from several threads while it is being written: analyses and the UI run during a scan,
/// and deletions change the tree while analyses run. These tests hammer exactly the combinations the app allows
/// and fail on any exception, endless loop or broken total.
/// </summary>
public class ConcurrencyStressTests(ITestOutputHelper output)
{
    /// <summary>DISKSCOPE_STRESS=1 runs the long versions (about a minute); the normal suite runs short ones.</summary>
    static bool Long => Environment.GetEnvironmentVariable("DISKSCOPE_STRESS") == "1";

    /// <summary>Everything the app may do with a tree from a background thread or the UI thread while it changes.</summary>
    static void ReadEverything(NodeStore s, NodeStore baseline, Random r, string snapshotFile)
    {
        int count = s.Count;
        if (count < 2 || s.Root == NodeStore.None) return;
        var rows = new TreeRows(s) { Sort = (SortColumn)r.Next(5), Descending = r.Next(2) == 0 };
        for (int i = 0; i < 20; i++)
        {
            int n = r.Next(1, count);
            if (s[n].IsDirectory) rows.SetExpanded(n, true);
            else if (r.Next(10) == 0) rows.Reveal(n);
        }
        rows.Rebuild();
        for (int i = 0; i < 50; i++) _ = s.GetPath(r.Next(1, count));
        _ = Search.Run(s, new SearchQuery { Name = r.Next(2) == 0 ? "*5*" : null, Kind = (SearchKind)r.Next(4), Limit = 200 });
        _ = CleanupFinder.Find(s, CleanupCatalog.Default());
        _ = FolderSummary.Build(s, s.Root, 10);
        _ = TreemapLayout.Build(s, s.Root, new RectF(0, 0, 1200, 800));
        var cmp = new TreeComparison(s, baseline);
        var compared = new TreeRows(s) { Sort = SortColumn.Delta, DeltaOf = cmp.Delta, Filter = x => cmp.Delta(x) != 0 };
        compared.SetExpanded(s.Root, true);
        compared.Rebuild();
        _ = new OrphanFinder().Find(s, [new InstalledProgram("Something", null, null)], new HashSet<string> { "a_b" }, [], DateTime.UtcNow);
        // The app saves one snapshot at a time; each reader thread gets its own file.
        if (r.Next(4) == 0) Snapshot.Save(s, snapshotFile + Environment.CurrentManagedThreadId, DateTime.UtcNow, TimeSpan.Zero, ScanMode.Directory, true, 0);
    }

    static void RunReaders(int threads, Func<bool> keepGoing, Action<Random> work, ConcurrentQueue<Exception> errors)
    {
        var readers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var r = new Random(t * 7919);
            while (keepGoing())
            {
                try { work(r); }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        }) { IsBackground = true }).ToList();
        readers.ForEach(t => t.Start());
        readers.ForEach(t => Assert.True(t.Join(TimeSpan.FromMinutes(3)), "a reader hung (endless loop?)"));
    }

    /// <summary>Sizes, counts and newest times of every live folder must equal what is under it.</summary>
    static void AssertTotalsConsistent(NodeStore s)
    {
        var stack = new Stack<(int Node, bool Done)>();
        var sums = new Dictionary<int, (long Size, int Files, int Dirs)>();
        stack.Push((s.Root, false));
        while (stack.Count > 0)
        {
            var (n, done) = stack.Pop();
            if (!done)
            {
                stack.Push((n, true));
                for (int c = s[n].FirstChild; c != NodeStore.None; c = s[c].NextSibling)
                    if (s[c].IsDirectory) stack.Push((c, false));
                continue;
            }
            long size = 0;
            int files = 0, dirs = 0;
            for (int c = s[n].FirstChild; c != NodeStore.None; c = s[c].NextSibling)
            {
                Assert.True(s.IsLive(c), $"a removed node {c} is still linked under {n}");
                if (s[c].IsDirectory)
                {
                    var sub = sums[c];
                    size += sub.Size;
                    files += sub.Files;
                    dirs += sub.Dirs + 1;
                }
                else
                {
                    size += s[c].Size;
                    files++;
                }
            }
            Assert.Equal(size, s[n].Size);
            Assert.Equal(files, s[n].FileCount);
            Assert.Equal(dirs, s[n].DirCount);
            sums[n] = (size, files, dirs);
        }
    }

    static NodeStore Baseline()
    {
        var b = new NodeStore();
        int root = b.CreateRoot(@"C:\stress");
        int d = b.Add(root, "d1", NodeFlags.Directory, 0, 0, 0, 0);
        b.PublishChildren(root, d);
        b.ComputeTotalsFromRoot();
        return b;
    }

    /// <summary>
    /// Builds a tree the way the directory scanner does: several threads each list a "folder", link its children
    /// privately, publish them and add their totals to the ancestors.
    /// </summary>
    static List<Thread> StartWriters(NodeStore s, int threads, int target, int seed, ConcurrentQueue<Exception> errors)
    {
        var work = new BlockingCollection<(int Dir, int Depth)> { (s.Root, 0) };
        int pending = 1;
        var writers = Enumerable.Range(0, threads).Select(w => new Thread(() =>
        {
            var r = new Random(seed * 100 + w);
            try
            {
                foreach (var (dir, depth) in work.GetConsumingEnumerable())
                {
                    int head = NodeStore.None, files = 0, dirs = 0;
                    long size = 0;
                    var subdirs = new List<int>();
                    int width = depth < 3 ? 40 : 12;
                    for (int i = 0; i < width; i++)
                    {
                        bool isDir = depth < 7 && i % 4 == 0 && s.Count < target;
                        long len = isDir ? 0 : r.Next(1, 1 << 20);
                        int n = s.Add(dir, (isDir ? "d" : "f") + i + "_" + r.Next(10), isDir ? NodeFlags.Directory : NodeFlags.None, 0, len, len,
                            DateTime.UtcNow.AddDays(-r.Next(1000)).ToFileTimeUtc());
                        s[n].NextSibling = head;
                        head = n;
                        if (isDir) { dirs++; subdirs.Add(n); } else { files++; size += len; }
                    }
                    s.PublishChildren(dir, head);
                    s.AddToAncestors(dir, size, size, files, dirs);
                    s.SetFlag(dir, NodeFlags.Scanned);
                    foreach (int d in subdirs)
                    {
                        Interlocked.Increment(ref pending);
                        work.Add((d, depth + 1));
                    }
                    if (Interlocked.Decrement(ref pending) == 0) work.CompleteAdding();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); work.CompleteAdding(); }
        }) { IsBackground = true }).ToList();
        writers.ForEach(t => t.Start());
        return writers;
    }

    /// <summary>What table walks do (search, cleanup, duplicates): visit every index below Count.</summary>
    static void WalkTable(NodeStore s)
    {
        int count = s.Count;
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref s[i];
            if ((e.Flags & NodeFlags.Used) != 0) sum += e.Size + s.GetName(i).Length;
        }
        GC.KeepAlive(sum);
    }

    [Fact]
    public void AnalysesWhileManyThreadsBuildTheTree()
    {
        // Crosses many node-chunk boundaries (every 65 536 nodes) while readers walk the whole table and the tree.
        var errors = new ConcurrentQueue<Exception>();
        var baseline = Baseline();
        string snap = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        long total = 0;
        int rounds = Long ? 12 : 2;
        for (int round = 0; round < rounds; round++)
        {
            var s = new NodeStore();
            s.CreateRoot(@"C:\stress");
            var writers = StartWriters(s, 8, 1_000_000, round, errors);
            bool writing = true;
            var walkers = new Thread(() => RunReaders(4, () => Volatile.Read(ref writing), _ => WalkTable(s), errors));
            var readers = new Thread(() => RunReaders(4, () => Volatile.Read(ref writing),
                r => { ReadEverything(s, baseline, r, snap); s.SetFlag(r.Next(1, Math.Max(2, s.Count)), NodeFlags.Queued); }, errors));
            walkers.Start();
            readers.Start();
            writers.ForEach(t => Assert.True(t.Join(TimeSpan.FromMinutes(3)), "a writer hung"));
            Volatile.Write(ref writing, false);
            walkers.Join();
            readers.Join();
            AssertTotalsConsistent(s);
            total += s.Count;
        }
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(snap) + "*")) File.Delete(f);

        output.WriteLine($"{total:N0} nodes in {rounds} rounds; {errors.Count} errors");
        foreach (var e in errors.Take(5)) output.WriteLine(e.ToString());
        Assert.Empty(errors);
    }

    [Fact]
    public void SortingWhileTotalsChange()
    {
        // During a scan the totals of folders already shown keep growing; sorting, the treemap, the folder summary
        // and the comparison view read them at the same time.
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\stress");
        var dirs = new List<int>();
        int head = NodeStore.None;
        for (int i = 0; i < 3000; i++)
        {
            bool isDir = i % 2 == 0;
            int n = s.Add(root, "e" + i, isDir ? NodeFlags.Directory : NodeFlags.None, 0, isDir ? 0 : i, i, i);
            s[n].NextSibling = head;
            head = n;
            if (isDir) dirs.Add(n);
        }
        s.PublishChildren(root, head);
        s.ComputeTotalsFromRoot();
        var baseline = Baseline();
        var cmp = new TreeComparison(s, baseline);
        var errors = new ConcurrentQueue<Exception>();
        bool running = true;
        var changers = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            var r = new Random(t);
            while (Volatile.Read(ref running))
            {
                int d = dirs[r.Next(dirs.Count)];
                long delta = r.Next(1, 1 << 30);
                s.AddToAncestors(d, delta, delta, 1, 0);
                s.MaxTimesToAncestors(d, r.NextInt64(1, long.MaxValue / 4), r.NextInt64(1, long.MaxValue / 4));
            }
        })).ToList();
        changers.ForEach(t => t.Start());
        var stop = DateTime.UtcNow.AddSeconds(Long ? 8 : 3);
        var readerThread = new Thread(() => RunReaders(6, () => DateTime.UtcNow < stop, r =>
        {
            var rows = new TreeRows(s) { Sort = (SortColumn)r.Next(7), Descending = r.Next(2) == 0, DeltaOf = cmp.Delta };
            _ = rows.SortedChildren(root);
            _ = TreemapLayout.Build(s, root, new RectF(0, 0, 1600, 1000));
            _ = FolderSummary.Build(s, root, 20);
        }, errors));
        readerThread.Start();
        readerThread.Join();
        Volatile.Write(ref running, false);
        changers.ForEach(t => t.Join());
        foreach (var e in errors.Take(5)) output.WriteLine(e.ToString());
        Assert.Empty(errors);
    }

    [Fact]
    public void AnalysesDuringRealScans()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var errors = new ConcurrentQueue<Exception>();
        var baseline = Baseline();
        string snap = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        for (int round = 0, rounds = Long ? 3 : 1; round < rounds; round++)
        {
            var session = new ScanSession(windows, ScanMode.Directory);
            session.Start();
            RunReaders(6, () => session.State == ScanState.Running, r => ReadEverything(session.Store, baseline, r, snap + r.Next(3)), errors);
            session.Wait();
            output.WriteLine($"round {round}: {session.Store.Count:N0} nodes, {errors.Count} errors so far");
        }
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(snap) + "*")) File.Delete(f);
        foreach (var e in errors.Take(5)) output.WriteLine(e.ToString());
        Assert.Empty(errors);
    }

    [Fact]
    public void AnalysesWhileDeletionsChangeTheTree()
    {
        // After a scan: the UI thread removes batches of finished deletions while analyses run in the background.
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\stress");
        var all = new List<int>();
        var r0 = new Random(1);
        void Build(int dir, int depth)
        {
            int head = NodeStore.None;
            for (int i = 0; i < (depth < 3 ? 30 : 10); i++)
            {
                bool isDir = depth < 5 && i % 3 == 0;
                long len = isDir ? 0 : r0.Next(1, 100_000);
                int n = s.Add(dir, (isDir ? "d" : "f") + i, isDir ? NodeFlags.Directory : NodeFlags.None, 0, len, len, 1);
                s[n].NextSibling = head;
                head = n;
                all.Add(n);
                if (isDir) Build(n, depth + 1);
            }
            s.PublishChildren(dir, head);
        }
        Build(root, 0);
        s.ComputeTotalsFromRoot();
        var baseline = Baseline();
        var errors = new ConcurrentQueue<Exception>();
        string snap = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);

        bool deleting = true;
        var readerThread = new Thread(() => RunReaders(6, () => Volatile.Read(ref deleting), r => ReadEverything(s, baseline, r, snap + r.Next(3)), errors));
        readerThread.Start();
        var r = new Random(2);
        for (int batch = 0; batch < 200 && s[root].FirstChild != NodeStore.None; batch++)
        {
            s.RemoveMany(Enumerable.Range(0, r.Next(1, 500)).Select(_ => all[r.Next(all.Count)]));
            Thread.Sleep(5);
        }
        Volatile.Write(ref deleting, false);
        readerThread.Join();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(snap) + "*")) File.Delete(f);

        output.WriteLine($"{all.Count:N0} nodes, {all.Count(s.IsLive):N0} left; {errors.Count} errors");
        foreach (var e in errors.Take(5)) output.WriteLine(e.ToString());
        Assert.Empty(errors);
        AssertTotalsConsistent(s);
    }
}
