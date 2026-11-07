using System.IO.Compression;
using System.Text;
using DiskScope.Core.Scanning;

namespace DiskScope.Core.Storage;

/// <summary>What a snapshot file says about itself, readable without loading the tree.</summary>
public sealed record SnapshotInfo(
    string FilePath,
    string RootPath,
    DateTime ScanTimeUtc,
    TimeSpan Duration,
    ScanMode Mode,
    bool Complete,
    long Nodes,
    long Files,
    long Directories,
    long Size,
    long Length,
    long Errors)
{
    public long FileBytes => new FileInfo(FilePath).Length;
}

/// <summary>
/// Saved scan: a small uncompressed header with the totals, then the tree in preorder, Deflate-compressed.
/// Directory totals are not stored; they are recomputed on load.
/// </summary>
public static class Snapshot
{
    public const string Extension = ".dscan";
    static readonly byte[] Magic = "DSCN"u8.ToArray();
    /// <summary>1: first format; 2: adds last access times.</summary>
    const ushort FormatVersion = 2;

    public static void Save(NodeStore store, string filePath, DateTime scanTimeUtc, TimeSpan duration, ScanMode mode,
        bool complete, long errors, Action<double>? progress = null) =>
        Save(store, filePath, scanTimeUtc, duration, mode, complete, errors, progress, FormatVersion);

    /// <summary>Writing an older format version is only for testing that it can still be read.</summary>
    internal static void Save(NodeStore store, string filePath, DateTime scanTimeUtc, TimeSpan duration, ScanMode mode,
        bool complete, long errors, Action<double>? progress, ushort formatVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temp = filePath + ".tmp";
        ref var root = ref store[store.Root];
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var header = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
            header.Write(Magic);
            header.Write(formatVersion);
            header.Write(store.RootPath);
            header.Write(scanTimeUtc.Ticks);
            header.Write(duration.Ticks);
            header.Write((byte)mode);
            header.Write(complete);
            header.Write((long)CountLive(store));
            header.Write((long)root.FileCount);
            header.Write((long)root.DirCount);
            header.Write(root.Size);
            header.Write(root.Length);
            header.Write(errors);
            header.Flush();

            using var deflate = new DeflateStream(fs, CompressionLevel.Fastest, leaveOpen: true);
            using var buffered = new BufferedStream(deflate, 1 << 20);
            var w = new BinaryWriter(buffered);
            WriteTree(store, w, root.FileCount + root.DirCount + 1, progress, formatVersion);
            w.Flush();
        }
        File.Move(temp, filePath, overwrite: true);
    }

    static int CountLive(NodeStore store)
    {
        int count = 0;
        var stack = new Stack<int>();
        stack.Push(store.Root);
        while (stack.Count > 0)
        {
            count++;
            for (int c = store[stack.Pop()].FirstChild; c != NodeStore.None; c = store[c].NextSibling)
                if (store[c].IsDirectory) stack.Push(c); else count++;
        }
        return count;
    }

    static void WriteTree(NodeStore store, BinaryWriter w, long total, Action<double>? progress, ushort version)
    {
        long written = 0;
        var stack = new Stack<int>();
        var children = new List<int>();
        stack.Push(store.Root);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            ref var n = ref store[node];
            var name = store.RawName(node, out ushort encodedLen);
            w.Write7BitEncodedInt(encodedLen);
            w.Write(name);
            w.Write7BitEncodedInt((int)(n.Flags & ~NodeFlags.Used));
            w.Write7BitEncodedInt((int)n.Attributes);
            w.Write(n.MTime);
            if (version >= 2) w.Write(n.ATime);
            if (n.IsDirectory)
            {
                children.Clear();
                for (int c = n.FirstChild; c != NodeStore.None; c = store[c].NextSibling) children.Add(c);
                w.Write7BitEncodedInt(children.Count);
                // Pushed in reverse so they come off the stack (and into the file) in list order.
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            else
            {
                w.Write7BitEncodedInt64(n.Size);
                w.Write7BitEncodedInt64(n.Length);
            }
            if (progress != null && ++written % 500_000 == 0) progress((double)written / total);
        }
    }

    public static SnapshotInfo ReadInfo(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return ReadHeader(fs, filePath, out _);
    }

    static SnapshotInfo ReadHeader(Stream fs, string filePath, out ushort version)
    {
        var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a DiskScope snapshot.");
        version = r.ReadUInt16();
        if (version is < 1 or > FormatVersion) throw new InvalidDataException($"Unsupported snapshot version {version}.");
        try
        {
            return new SnapshotInfo(filePath, r.ReadString(), new DateTime(r.ReadInt64(), DateTimeKind.Utc), new TimeSpan(r.ReadInt64()),
                (ScanMode)r.ReadByte(), r.ReadBoolean(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Damaged snapshot header.", ex); // e.g. a date out of range
        }
    }

    /// <summary>Loads a saved scan. A damaged file fails with <see cref="InvalidDataException"/> (or an I/O error).</summary>
    public static NodeStore Load(string filePath, out SnapshotInfo info, Action<double>? progress = null, CancellationToken cancel = default)
    {
        try
        {
            return LoadCore(filePath, out info, progress, cancel);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException("Damaged snapshot: " + ex.Message, ex);
        }
    }

    static NodeStore LoadCore(string filePath, out SnapshotInfo info, Action<double>? progress, CancellationToken cancel)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        info = ReadHeader(fs, filePath, out ushort version);
        using var deflate = new DeflateStream(fs, CompressionMode.Decompress);
        using var buffered = new BufferedStream(deflate, 1 << 20);
        var r = new BinaryReader(buffered);
        var store = new NodeStore();

        // Each frame: a directory, how many of its children are still to be read, and its last child so far.
        var stack = new Stack<(int Dir, int Remaining, int Last)>();
        var nameBuf = new byte[1 << 16];
        long read = 0;
        do
        {
            int parent = NodeStore.None;
            if (stack.Count > 0)
            {
                var top = stack.Pop();
                parent = top.Dir;
                stack.Push(top with { Remaining = top.Remaining - 1 });
            }

            ushort encodedLen = (ushort)r.Read7BitEncodedInt();
            int nameBytes = encodedLen & 0x7FFF;
            r.BaseStream.ReadExactly(nameBuf, 0, nameBytes);
            CheckName(nameBuf.AsSpan(0, nameBytes), (encodedLen & 0x8000) != 0, isRoot: parent == NodeStore.None);
            // Only flags describing the item are kept; state flags (deleted, queued) never come from a file.
            var flags = (NodeFlags)r.Read7BitEncodedInt() & PersistentFlags;
            uint attrs = (uint)r.Read7BitEncodedInt();
            long mtime = r.ReadInt64();
            long atime = version >= 2 ? r.ReadInt64() : 0;
            bool isDir = (flags & NodeFlags.Directory) != 0;
            int childCount = 0;
            long size = 0, length = 0;
            if (isDir) childCount = r.Read7BitEncodedInt();
            else
            {
                size = r.Read7BitEncodedInt64();
                length = r.Read7BitEncodedInt64();
                if (size < 0 || length < 0) throw new InvalidDataException("Negative file size in snapshot.");
            }

            int node = store.AddRaw(parent, nameBuf.AsSpan(0, nameBytes), encodedLen, flags, attrs, size, length, mtime, atime);
            if (parent != NodeStore.None)
            {
                var top = stack.Pop();
                if (top.Last == NodeStore.None) store[parent].FirstChild = node;
                else store[top.Last].NextSibling = node;
                stack.Push(top with { Last = node });
            }
            if (isDir && childCount > 0) stack.Push((node, childCount, NodeStore.None));
            while (stack.Count > 0 && stack.Peek().Remaining == 0) stack.Pop();

            if (++read % 500_000 == 0)
            {
                progress?.Invoke((double)read / Math.Max(1, info.Nodes));
                cancel.ThrowIfCancellationRequested();
            }
        } while (stack.Count > 0);

        store.RecomputeTotals();
        return store;
    }

    const NodeFlags PersistentFlags = NodeFlags.Directory | NodeFlags.Scanned | NodeFlags.Error | NodeFlags.Reparse | NodeFlags.HardLink;

    /// <summary>
    /// Rejects names that are not a single path component, so that a crafted snapshot cannot make the paths built
    /// from it (for opening or deleting) point outside the tree it shows. The root must be a full path.
    /// </summary>
    static void CheckName(ReadOnlySpan<byte> raw, bool utf16, bool isRoot)
    {
        if (utf16 && raw.Length % 2 != 0) throw new InvalidDataException("Broken name in snapshot.");
        string name = utf16 ? new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(raw)) : Encoding.Latin1.GetString(raw);
        bool ok = isRoot ? Path.IsPathFullyQualified(name) && !name.Contains("..") : NodeStore.IsValidName(name);
        if (!ok) throw new InvalidDataException($"Invalid name in snapshot: \"{name}\".");
    }
}
