using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskScope.Core.Duplicates;

/// <summary>Files with identical content; all but one are wasted space.</summary>
public sealed record DuplicateGroup(long Size, List<int> Nodes)
{
    public long Wasted => Size * (Nodes.Count - 1);
}

public enum DuplicateStage
{
    Collecting,
    Sizing,
    Sampling,
    Hashing,
    Done,
}

/// <summary>Live counters for the UI.</summary>
public sealed class DuplicateProgress
{
    public volatile DuplicateStage Stage;
    public long FilesTotal;
    public long FilesDone;
    public long BytesTotal;
    public long BytesDone;

    /// <summary>Result of the size pass: files whose actual size matches another file's, before any content is read.</summary>
    public long SizeGroups;
    public long SizeFiles;
    public long SizeBytes;
}

/// <summary>
/// Finds files with the same content, each pass cheaper than the next and run only on what the previous one left:
/// equal size in the scan, equal actual size on disk now (files changed or deleted since the scan drop out),
/// equal hash of the first and last 64 KB, equal hash of the whole file. Hard links (one file under several names)
/// are recognised by file ID and not reported; cloud-only files are skipped so that nothing gets downloaded.
/// </summary>
public sealed class DuplicateFinder
{
    const int SampleSize = 64 * 1024;
    const uint CloudOrOffline = 0x1000 | 0x40000 | 0x400000; // OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS

    public long MinSize { get; init; } = 1 << 20;
    public int Scope { get; init; } = NodeStore.None;
    /// <summary>Files read at once; a few in parallel help SSDs and do not hurt much on disks.</summary>
    public int Parallelism { get; init; } = 4;

    public List<DuplicateGroup> Find(NodeStore store, DuplicateProgress progress, CancellationToken cancel = default)
    {
        progress.Stage = DuplicateStage.Collecting;
        var bySize = CollectBySize(store, cancel);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancel };
        object sync = new();

        // Pass 2: the actual size on disk now, without opening the files. Only files whose size in the scan matched
        // another file's are checked; they are regrouped by what the disk says.
        var scanned = bySize.Where(g => g.Count > 1).SelectMany(g => g).ToList();
        progress.Stage = DuplicateStage.Sizing;
        progress.FilesTotal = scanned.Count;
        var actual = new Dictionary<int, long>();
        Parallel.ForEach(scanned, options, node =>
        {
            if (ActualSize(store.GetPath(node)) is long size && size >= MinSize)
                lock (sync) actual[node] = size;
            Interlocked.Increment(ref progress.FilesDone);
        });
        var candidates = actual.GroupBy(x => x.Value, x => x.Key).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        progress.SizeGroups = candidates.Count;
        progress.SizeFiles = candidates.Sum(g => g.Count);
        progress.SizeBytes = candidates.Sum(g => g.Count * actual[g[0]]);

        // Pass 3: same sample (first and last 64 KB); also drops hard links of one file.
        progress.Stage = DuplicateStage.Sampling;
        progress.FilesDone = 0;
        progress.FilesTotal = progress.SizeFiles;
        var sampled = new List<List<FileEntry>>();
        Parallel.ForEach(candidates, options, group =>
        {
            var entries = new List<FileEntry>();
            foreach (int node in group)
            {
                if (Read(store.GetPath(node), actual[node], sampleOnly: true) is FileEntry e) entries.Add(e with { Node = node });
                Interlocked.Increment(ref progress.FilesDone);
            }
            var unique = entries.GroupBy(e => e.FileId).Select(g => g.First()); // hard links of one file count once
            foreach (var same in unique.GroupBy(e => e.Hash).Where(g => g.Count() > 1))
                lock (sync) sampled.Add(same.ToList());
        });

        // Pass 4: whole-file hash, only where the sample could not tell (larger than the two samples).
        progress.Stage = DuplicateStage.Hashing;
        progress.FilesDone = 0;
        progress.FilesTotal = sampled.Sum(g => g.Count);
        progress.BytesTotal = sampled.Sum(g => g.Sum(e => actual[e.Node]));
        var result = new List<DuplicateGroup>();
        Parallel.ForEach(sampled, options, group =>
        {
            long size = actual[group[0].Node];
            IEnumerable<IGrouping<string, int>> same;
            if (size <= 2 * SampleSize)
            {
                same = group.GroupBy(e => e.Hash, e => e.Node);
                Interlocked.Add(ref progress.BytesDone, size * group.Count);
                Interlocked.Add(ref progress.FilesDone, group.Count);
            }
            else
            {
                var full = new List<(string Hash, int Node)>();
                foreach (var e in group)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Read(store.GetPath(e.Node), size, sampleOnly: false, progress, cancel) is FileEntry f) full.Add((f.Hash, e.Node));
                    Interlocked.Increment(ref progress.FilesDone);
                }
                same = full.GroupBy(x => x.Hash, x => x.Node);
            }
            foreach (var g in same.Where(g => g.Count() > 1))
                lock (sync) result.Add(new DuplicateGroup(store[g.First()].Size, g.ToList()));
        });

        progress.Stage = DuplicateStage.Done;
        return result.OrderByDescending(g => g.Wasted).ToList();
    }

    List<List<int>> CollectBySize(NodeStore store, CancellationToken cancel)
    {
        var bySize = new Dictionary<long, List<int>>();
        int scope = Scope == NodeStore.None ? store.Root : Scope;
        int count = store.Count;
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            ref var n = ref store[i];
            if (n.IsDirectory || (n.Flags & (NodeFlags.Used | NodeFlags.Deleted)) != NodeFlags.Used) continue;
            if ((n.Flags & (NodeFlags.HardLink | NodeFlags.Reparse | NodeFlags.Queued)) != 0) continue;
            if (n.Length < MinSize || (n.Attributes & CloudOrOffline) != 0) continue;
            if (scope != store.Root && !IsUnder(store, i, scope)) continue;
            if (!bySize.TryGetValue(n.Length, out var list)) bySize[n.Length] = list = [];
            list.Add(i);
        }
        return [.. bySize.Values];
    }

    static bool IsUnder(NodeStore store, int node, int scope)
    {
        for (int p = store[node].Parent; p != NodeStore.None; p = store[p].Parent)
            if (p == scope) return true;
        return false;
    }

    /// <summary>The file's current size, or null when it is gone, unreadable or only in the cloud.</summary>
    static long? ActualSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || ((uint)info.Attributes & CloudOrOffline) != 0) return null;
            return info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    readonly record struct FileEntry(int Node, (ulong Volume, UInt128 Id) FileId, string Hash);

    /// <summary>Hashes the file's samples or whole content; null when it cannot be read (locked, gone, denied).</summary>
    static FileEntry? Read(string path, long size, bool sampleOnly, DuplicateProgress? progress = null, CancellationToken cancel = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                sampleOnly ? FileOptions.RandomAccess : FileOptions.SequentialScan);
            var id = FileId(fs.SafeFileHandle);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[sampleOnly ? SampleSize : 1 << 20];
            if (sampleOnly)
            {
                hash.AppendData(buffer, 0, fs.ReadAtLeast(buffer, Math.Min(SampleSize, (int)Math.Min(size, SampleSize)), false));
                if (size > SampleSize)
                {
                    fs.Seek(Math.Max(SampleSize, size - SampleSize), SeekOrigin.Begin);
                    hash.AppendData(buffer, 0, fs.ReadAtLeast(buffer, buffer.Length, false));
                }
            }
            else
            {
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    if (progress != null) Interlocked.Add(ref progress.BytesDone, read);
                }
            }
            return new FileEntry(NodeStore.None, id, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Two ulongs rather than UInt128: UInt128 is 16-byte aligned in .NET and would shift the field.
    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ID_INFO info, int size);

    const int FileIdInfo = 18;

    static long _unknownIds;

    static (ulong, UInt128) FileId(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, FileIdInfo, out var info, Marshal.SizeOf<FILE_ID_INFO>())
            ? (info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow))
            // Unknown (some file systems): a fresh value, so every such file counts as distinct. Handle values are
            // reused after closing and would make unrelated files look like one.
            : (ulong.MaxValue, (UInt128)(ulong)Interlocked.Increment(ref _unknownIds));
}
