using System.Diagnostics;
using DiskScope.Core;
using DiskScope.Core.Cleanup;
using DiskScope.Core.Orphans;
using DiskScope.Core.Scanning;
using DiskScope.Core.Storage;
using Xunit.Abstractions;

namespace DiskScope.Tests;

/// <summary>
/// Everything the app does with a scan, on a tree as large as a full disk: tens of millions of entries, a folder with
/// a million files, deep chains. Looks for crashes, overflows and slow (quadratic) steps that small trees never show.
/// Runs only with DISKSCOPE_STRESS=1 (it needs a few GB of memory and minutes of time);
/// DISKSCOPE_STRESS_NODES sets the size (default 12 million; the comparison step holds two trees of that size).
/// </summary>
public class SizeStressTests(ITestOutputHelper output)
{
    static bool Enabled => Environment.GetEnvironmentVariable("DISKSCOPE_STRESS") == "1";

    static int TargetNodes => int.TryParse(Environment.GetEnvironmentVariable("DISKSCOPE_STRESS_NODES"), out int n) ? n : 12_000_000;

    static readonly string[] Extensions = [".dat", ".log", ".txt", ".jpg", ".dll", ".tmp", ".json", ".cache", ""];
    static readonly string[] SpecialFolders = ["node_modules", "Temp", "cache", "obj", ".git", "__pycache__"];

    readonly Stopwatch _watch = new();

    T Step<T>(string name, Func<T> work, double maxSeconds = 120)
    {
        _watch.Restart();
        var result = work();
        _watch.Stop();
        output.WriteLine($"{name,-44} {_watch.Elapsed.TotalSeconds,7:F2} s   managed {GC.GetTotalMemory(false) / 1048576,6:N0} MB");
        Assert.True(_watch.Elapsed.TotalSeconds < maxSeconds, $"{name} took {_watch.Elapsed.TotalSeconds:F0} s");
        return result;
    }

    void Step(string name, Action work, double maxSeconds = 120) => Step(name, () => { work(); return 0; }, maxSeconds);

    /// <summary>
    /// A disk-like tree: one folder of a million files (an extreme but real case: mail stores, caches), a chain
    /// 2 000 folders deep, and the rest as folders of ~25 files and a few subfolders, with names in several
    /// encodings and the folder names cleanup rules look for.
    /// </summary>
    static (NodeStore Store, int Huge, int HugeCount) BuildDiskLike(int target)
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        var r = new Random(42);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        long Time() => now - r.NextInt64(0, 3650L * 864_000_000_000);

        int Child(int parent, string name, bool dir, ref int head)
        {
            long len = dir ? 0 : r.Next(0, 4) == 0 ? r.NextInt64(1, 4L << 30) : r.Next(0, 200_000);
            int n = s.Add(parent, name, dir ? NodeFlags.Directory : NodeFlags.None, 0, (len + 4095) & ~4095L, len, Time(), Time());
            s[n].NextSibling = head;
            head = n;
            return n;
        }

        int rootHead = NodeStore.None;
        int hugeCount = Math.Min(1_000_000, target / 12);
        int huge = Child(root, "Huge", true, ref rootHead);
        int hugeHead = NodeStore.None;
        for (int i = 0; i < hugeCount; i++) Child(huge, $"msg{i:D7}.eml", false, ref hugeHead);
        s.PublishChildren(huge, hugeHead);

        int deep = Child(root, "Deep", true, ref rootHead);
        for (int d = 0; d < 2000; d++)
        {
            int head = NodeStore.None;
            Child(deep, "file.txt", false, ref head);
            int next = Child(deep, "level" + d, true, ref head);
            s.PublishChildren(deep, head);
            deep = next;
        }

        var queue = new Queue<int>();
        for (int i = 0; i < 20; i++) queue.Enqueue(Child(root, "Dir" + i, true, ref rootHead));
        s.PublishChildren(root, rootHead);
        while (queue.Count > 0)
        {
            int dir = queue.Dequeue();
            int head = NodeStore.None;
            int files = r.Next(5, 45);
            for (int i = 0; i < files && s.Count < target; i++)
            {
                string name = (i % 50) switch
                {
                    0 => $"soubor_čšž_{i}.txt",
                    1 => $"文件_{i}.doc",
                    _ => $"file_{r.Next(100_000)}{Extensions[r.Next(Extensions.Length)]}",
                };
                Child(dir, name, false, ref head);
            }
            if (s.Count < target)
                for (int i = 0, n = r.Next(2, 6); i < n; i++)
                    queue.Enqueue(Child(dir, r.Next(40) == 0 ? SpecialFolders[r.Next(SpecialFolders.Length)] : "folder" + i, true, ref head));
            s.PublishChildren(dir, head);
        }
        s.ComputeTotalsFromRoot();
        return (s, huge, hugeCount);
    }

    [Fact]
    public void EverythingOnADiskSizedTree()
    {
        if (!Enabled)
        {
            output.WriteLine("Skipped: set DISKSCOPE_STRESS=1 to run.");
            return;
        }
        int target = TargetNodes;
        var (s, huge, hugeCount) = Step($"build {target:N0} nodes", () => BuildDiskLike(target));
        int root = s.Root;
        output.WriteLine($"{s.Count:N0} nodes, store {s.MemoryUsage / 1048576:N0} MB, root {Format.Size(s[root].Size, System.Globalization.CultureInfo.InvariantCulture)}, " +
                         $"{s[root].FileCount:N0} files, {s[root].DirCount:N0} folders");
        Assert.Equal(s.Count - 1, s[root].FileCount + s[root].DirCount);

        Step("totals recomputed", () => s.ComputeTotalsFromRoot());
        long rootSize = s[root].Size;

        foreach (var kind in Enum.GetValues<SearchKind>())
            Step($"search {kind}", () => Assert.NotEmpty(Search.Run(s, new SearchQuery { Kind = kind }).Nodes));
        Step("search name *.log;*.tmp, older than a year", () => Assert.NotEmpty(Search.Run(s,
            new SearchQuery { Name = "*.log;*.tmp", ModifiedBefore = DateTime.UtcNow.AddYears(-1), AccessedBefore = DateTime.UtcNow.AddYears(-1) }).Nodes));
        Step("search substring čšž", () => Assert.NotEmpty(Search.Run(s, new SearchQuery { Name = "čšž", Kind = SearchKind.All }).Nodes));

        Step("cleanup catalog", () => Assert.NotEmpty(CleanupFinder.Find(s, CleanupCatalog.Default())));
        Step("orphans", () => new OrphanFinder().Find(s, [new InstalledProgram("Some App", null, null)], new HashSet<string>(), [], DateTime.UtcNow));
        Step("folder summary of the drive", () => FolderSummary.Build(s, root, 20));
        Step("treemap of the drive", () => TreemapLayout.Build(s, root, new RectF(0, 0, 2560, 1440)));
        Step("treemap of the million-file folder", () => TreemapLayout.Build(s, huge, new RectF(0, 0, 2560, 1440)));

        var rows = new TreeRows(s);
        rows.SetExpanded(root, true);
        rows.SetExpanded(huge, true);
        foreach (var sort in Enum.GetValues<SortColumn>().Where(c => c != SortColumn.Delta))
            Step($"rows with the million-file folder, by {sort}", () =>
            {
                rows.Sort = sort;
                rows.Rebuild();
                Assert.True(rows.Count > hugeCount);
            });
        Step("expand every folder (whole tree as rows)", () =>
        {
            for (int i = 0; i < s.Count; i++)
                if (s[i].IsDirectory) rows.SetExpanded(i, true);
            rows.Sort = SortColumn.Size;
            rows.Rebuild();
            Assert.Equal(s.Count, rows.Count); // the view root is a row too
        });
        Step("find rows of 100 000 selected items", () =>
        {
            var selected = Enumerable.Range(0, 100_000).Select(i => rows.NodeAt(rows.Count - 1 - i * 7)).ToList();
            Assert.Equal(selected.Count, rows.RowsOf(selected).Count);
        }, maxSeconds: 10);
        rows = new TreeRows(s);

        string longest = Step("paths of 200 000 nodes", () =>
        {
            var r = new Random(1);
            string longest = "";
            for (int i = 0; i < 200_000; i++)
            {
                string p = s.GetPath(r.Next(1, s.Count));
                if (p.Length > longest.Length) longest = p;
            }
            return longest;
        });
        string deepest = FindDeepest(s);
        output.WriteLine($"deepest path {deepest.Length:N0} characters");
        Step("find path 2 000 levels deep", () => Assert.NotEqual(NodeStore.None, s.FindPath(deepest)));

        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Snapshot.Extension);
        try
        {
            Step("snapshot save", () => Snapshot.Save(s, file, DateTime.UtcNow, TimeSpan.FromHours(1), ScanMode.Mft, true, 0));
            output.WriteLine($"snapshot file {new FileInfo(file).Length / 1048576:N0} MB");
            int count = s.Count;
            string hugePath = s.GetPath(huge);
            // Comparing holds two trees; above 15 million nodes that does not fit a typical PC's free memory,
            // so the original is released and only loading is checked.
            bool compare = count <= 15_000_000;
            if (!compare)
            {
                s = null!;
                rows = null!;
                GC.Collect();
                output.WriteLine("original released; comparison skipped");
            }
            var loaded = Step("snapshot load", () => Snapshot.Load(file, out _));
            Assert.Equal(count, loaded.Count);
            Assert.Equal(rootSize, loaded[loaded.Root].Size);

            int loadedHuge = loaded.FindPath(hugePath);
            var doomed = new List<int>();
            for (int c = loaded[loadedHuge].FirstChild; c != NodeStore.None; c = loaded[c].NextSibling)
                if (doomed.Count < hugeCount / 2) doomed.Add(c);
            Step($"remove {doomed.Count:N0} files at once", () => loaded.RemoveMany(doomed));
            Assert.Equal(hugeCount - doomed.Count, loaded[loadedHuge].FileCount);

            if (!compare) return;
            var cmp = Step("comparison with the earlier scan", () => new TreeComparison(loaded, s));
            Assert.True(cmp.Delta(loaded.Root) < 0);
            Step("comparison rows (changed only)", () =>
            {
                var changed = new TreeRows(loaded) { Sort = SortColumn.Delta, DeltaOf = cmp.Delta, Filter = n => cmp.Delta(n) != 0 };
                changed.SetExpanded(loaded.Root, true);
                changed.SetExpanded(loadedHuge, true);
                changed.Rebuild();
                Assert.True(changed.Count >= 1);
            });
        }
        finally
        {
            File.Delete(file);
            output.WriteLine($"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB");
        }
    }

    /// <summary>The deepest path in the tree (the end of the 2 000-level chain).</summary>
    static string FindDeepest(NodeStore s)
    {
        int best = s.Root, bestDepth = 0;
        for (int i = 1; i < s.Count; i++)
        {
            int d = 0;
            for (int p = s[i].Parent; p != NodeStore.None && d <= bestDepth + 1; p = s[p].Parent) d++;
            if (d > bestDepth) { best = i; bestDepth = d; }
        }
        return s.GetPath(best);
    }
}
