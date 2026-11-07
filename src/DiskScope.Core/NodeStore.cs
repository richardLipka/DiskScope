using System.Runtime.InteropServices;
using System.Text;

namespace DiskScope.Core;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Used = 1,
    Directory = 2,
    /// <summary>The directory's own listing has been read (its subdirectories may still be pending).</summary>
    Scanned = 4,
    /// <summary>The directory could not be listed (access denied, vanished...).</summary>
    Error = 8,
    /// <summary>Junction or symbolic link; not followed.</summary>
    Reparse = 16,
    Deleted = 32,
    /// <summary>A further name (hard link) of a file listed elsewhere; its size is counted there.</summary>
    HardLink = 64,
    /// <summary>Waiting in the delete queue.</summary>
    Queued = 128,
}

/// <summary>
/// One file or directory. Kept as a plain struct in chunked arrays: no per-node objects,
/// so tens of millions of entries cost 64 bytes each plus the name.
/// Sizes, file and directory counts of a directory are aggregated over its whole subtree,
/// and its times are the newest found in it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Node
{
    /// <summary>Allocated size on disk.</summary>
    public long Size;
    /// <summary>Logical size (end of file).</summary>
    public long Length;
    /// <summary>
    /// Last write time as FILETIME (UTC). For a directory: the newest of its own time and of everything inside.
    /// </summary>
    public long MTime;
    /// <summary>
    /// Last access time as FILETIME (UTC), 0 when unknown. For a directory: the newest access to a file inside
    /// (its own access time is left out, as merely listing a directory updates it).
    /// </summary>
    public long ATime;
    public int Parent;
    public int FirstChild;
    public int NextSibling;
    public uint NameOffset;
    public int FileCount;
    public int DirCount;
    /// <summary>Encoded name length in bytes; the top bit marks UTF-16 encoding instead of Latin-1.</summary>
    public ushort NameLen;
    public NodeFlags Flags;
    public uint Attributes;

    public readonly bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

/// <summary>
/// Append-only, thread-safe storage of the scanned tree. Several scanner threads may allocate
/// nodes concurrently; each directory's children are written by a single thread and published
/// by one volatile write, so the UI thread can walk the tree while the scan is running.
/// </summary>
public sealed class NodeStore
{
    public const int None = -1;

    const int NodeShift = 16;
    const int NodeChunk = 1 << NodeShift;
    const int NodeMask = NodeChunk - 1;
    const int NameShift = 22;
    const int NameChunk = 1 << NameShift;
    const ushort Utf16Bit = 0x8000;

    readonly Node[]?[] _nodes = new Node[int.MaxValue / NodeChunk + 1][];
    readonly byte[]?[] _names = new byte[(1L << 32) / NameChunk][];
    readonly object _growLock = new();
    int _count;
    long _namePos;
    int _version;

    /// <summary>
    /// Whether a name is a single path component: not empty, not "." or "..", no separators, no colon (which would
    /// address an alternate stream or a drive) and no control characters. Names from saved scans and from raw MFT
    /// data are checked with this, because paths for deleting are built from them.
    /// </summary>
    public static bool IsValidName(ReadOnlySpan<char> name) =>
        name.Length > 0 && !name.SequenceEqual(".") && !name.SequenceEqual("..") &&
        name.IndexOfAny('\\', '/', ':') < 0 && name.IndexOfAnyInRange('\0', '\x1f') < 0;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Incremented whenever the visible tree changes; the UI uses it to decide when to refresh.</summary>
    public int Version => Volatile.Read(ref _version);

    public int Root { get; private set; } = None;

    public string RootPath => Root == None ? "" : GetName(Root);

    public ref Node this[int index] => ref _nodes[index >> NodeShift]![index & NodeMask];

    public long MemoryUsage
    {
        get
        {
            long nodeChunks = ((long)Count + NodeChunk - 1) / NodeChunk;
            long nameChunks = (Volatile.Read(ref _namePos) + NameChunk - 1) / NameChunk;
            return nodeChunks * NodeChunk * Marshal.SizeOf<Node>() + nameChunks * NameChunk;
        }
    }

    public int CreateRoot(string path, long mtime = 0)
    {
        if (Root != None) throw new InvalidOperationException("Root already exists.");
        int root = Allocate();
        ref var n = ref this[root];
        n.Parent = None;
        n.FirstChild = None;
        n.NextSibling = None;
        n.MTime = mtime;
        n.NameOffset = AddName(path, out n.NameLen);
        n.Flags = NodeFlags.Used | NodeFlags.Directory;
        Root = root;
        return root;
    }

    /// <summary>Allocates a node and stores its name; links are left for the caller to set.</summary>
    public int Add(int parent, ReadOnlySpan<char> name, NodeFlags flags, uint attributes, long size, long length, long mtime, long atime = 0)
    {
        int i = Allocate();
        ref var n = ref this[i];
        n.Parent = parent;
        n.FirstChild = None;
        n.NextSibling = None;
        n.Size = size;
        n.Length = length;
        n.MTime = mtime;
        n.ATime = atime;
        n.Attributes = attributes;
        n.NameOffset = AddName(name, out n.NameLen);
        Volatile.Write(ref Unsafe(ref n.Flags), (ushort)(flags | NodeFlags.Used));
        return i;
    }

    static ref ushort Unsafe(ref NodeFlags f) => ref System.Runtime.CompilerServices.Unsafe.As<NodeFlags, ushort>(ref f);

    /// <summary>Publishes a chain of children (already linked through NextSibling) under a parent.</summary>
    public void PublishChildren(int parent, int firstChild)
    {
        Volatile.Write(ref this[parent].FirstChild, firstChild);
        Interlocked.Increment(ref _version);
    }

    public void MarkChanged() => Interlocked.Increment(ref _version);

    /// <summary>Adds sizes and counts to a directory and all of its ancestors.</summary>
    public void AddToAncestors(int dir, long size, long length, int files, int dirs)
    {
        for (int i = dir; i != None; i = this[i].Parent)
        {
            ref var n = ref this[i];
            if (size != 0) Interlocked.Add(ref n.Size, size);
            if (length != 0) Interlocked.Add(ref n.Length, length);
            if (files != 0) Interlocked.Add(ref n.FileCount, files);
            if (dirs != 0) Interlocked.Add(ref n.DirCount, dirs);
        }
    }

    /// <summary>Raises the newest-change and newest-access times of a directory and its ancestors.</summary>
    public void MaxTimesToAncestors(int dir, long mtime, long atime)
    {
        for (int i = dir; i != None; i = this[i].Parent)
        {
            ref var n = ref this[i];
            bool raisedM = InterlockedMax(ref n.MTime, mtime);
            bool raisedA = InterlockedMax(ref n.ATime, atime);
            // Ancestors are never older than their descendants, so nothing above needs raising either.
            if (!raisedM && !raisedA) break;
        }
    }

    static bool InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return true;
        return false;
    }

    public void SetFlag(int index, NodeFlags flag)
    {
        ref ushort f = ref Unsafe(ref this[index].Flags);
        ushort old, upd;
        do
        {
            old = Volatile.Read(ref f);
            upd = (ushort)(old | (ushort)flag);
        } while (Interlocked.CompareExchange(ref f, upd, old) != old);
    }

    public void ClearFlag(int index, NodeFlags flag)
    {
        ref ushort f = ref Unsafe(ref this[index].Flags);
        ushort old, upd;
        do
        {
            old = Volatile.Read(ref f);
            upd = (ushort)(old & ~(ushort)flag);
        } while (Interlocked.CompareExchange(ref f, upd, old) != old);
    }

    public string GetName(int index)
    {
        ref var n = ref this[index];
        var span = NameBytes(n);
        return (n.NameLen & Utf16Bit) != 0
            ? new string(MemoryMarshal.Cast<byte, char>(span))
            : Encoding.Latin1.GetString(span);
    }

    /// <summary>Decodes the name into a buffer (at least 32767 chars covers any name) without allocating.</summary>
    public ReadOnlySpan<char> CopyName(int index, Span<char> buffer)
    {
        ref var n = ref this[index];
        var span = NameBytes(n);
        if ((n.NameLen & Utf16Bit) != 0)
        {
            var chars = MemoryMarshal.Cast<byte, char>(span);
            chars.CopyTo(buffer);
            return buffer[..chars.Length];
        }
        for (int i = 0; i < span.Length; i++) buffer[i] = (char)span[i];
        return buffer[..span.Length];
    }

    /// <summary>Case-insensitive name comparison without allocating.</summary>
    public bool NameEquals(int index, ReadOnlySpan<char> name)
    {
        ref var n = ref this[index];
        var span = NameBytes(n);
        if ((n.NameLen & Utf16Bit) != 0)
            return MemoryMarshal.Cast<byte, char>(span).Equals(name, StringComparison.OrdinalIgnoreCase);
        if (span.Length != name.Length) return false;
        for (int i = 0; i < span.Length; i++)
        {
            char a = (char)span[i], b = name[i];
            if (a != b && char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
        }
        return true;
    }

    ReadOnlySpan<byte> NameBytes(in Node n)
    {
        int len = n.NameLen & ~Utf16Bit;
        return new ReadOnlySpan<byte>(_names[n.NameOffset >> NameShift], (int)(n.NameOffset & (NameChunk - 1)), len);
    }

    /// <summary>The stored name bytes and their encoded length, for saving without re-encoding.</summary>
    internal ReadOnlySpan<byte> RawName(int index, out ushort encodedLen)
    {
        encodedLen = this[index].NameLen;
        return NameBytes(this[index]);
    }

    /// <summary>Adds a node from a saved name; used when loading a snapshot (single-threaded).</summary>
    internal int AddRaw(int parent, ReadOnlySpan<byte> rawName, ushort encodedLen, NodeFlags flags, uint attributes, long size, long length,
        long mtime, long atime)
    {
        int i = Allocate();
        ref var n = ref this[i];
        n.Parent = parent;
        n.FirstChild = None;
        n.NextSibling = None;
        n.Size = size;
        n.Length = length;
        n.MTime = mtime;
        n.ATime = atime;
        n.Attributes = attributes;
        n.NameOffset = ReserveName(rawName.Length, out var dest);
        rawName.CopyTo(dest);
        n.NameLen = encodedLen;
        n.Flags = flags | NodeFlags.Used;
        if (parent == None) Root = i;
        return i;
    }

    /// <summary>
    /// Fills in directory totals from their contents. Relies on every parent having a lower index
    /// than its children, which holds for a store loaded in preorder.
    /// </summary>
    internal void RecomputeTotals()
    {
        for (int i = Count - 1; i > 0; i--)
        {
            ref var n = ref this[i];
            if (n.Parent == None) continue;
            ref var p = ref this[n.Parent];
            p.Size += n.Size;
            p.Length += n.Length;
            p.MTime = Math.Max(p.MTime, n.MTime);
            p.ATime = Math.Max(p.ATime, n.ATime);
            if (n.IsDirectory)
            {
                p.FileCount += n.FileCount;
                p.DirCount += n.DirCount + 1;
            }
            else p.FileCount++;
        }
    }

    /// <summary>
    /// Fills in directory totals by a post-order walk from the root; works whatever the index order.
    /// Directories are marked scanned on the way. Nodes not reachable from the root are ignored.
    /// </summary>
    internal void ComputeTotalsFromRoot()
    {
        var stack = new Stack<(int Node, bool Done)>();
        stack.Push((Root, false));
        while (stack.Count > 0)
        {
            var (node, done) = stack.Pop();
            ref var n = ref this[node];
            if (!done)
            {
                stack.Push((node, true));
                for (int c = n.FirstChild; c != None; c = this[c].NextSibling)
                    if (this[c].IsDirectory) stack.Push((c, false));
                continue;
            }
            long size = 0, length = 0;
            int files = 0, dirs = 0;
            for (int c = n.FirstChild; c != None; c = this[c].NextSibling)
            {
                ref var ch = ref this[c];
                size += ch.Size;
                length += ch.Length;
                n.MTime = Math.Max(n.MTime, ch.MTime);
                n.ATime = Math.Max(n.ATime, ch.ATime);
                if (ch.IsDirectory)
                {
                    files += ch.FileCount;
                    dirs += ch.DirCount + 1;
                }
                else files++;
            }
            n.Size = size;
            n.Length = length;
            n.FileCount = files;
            n.DirCount = dirs;
            n.Flags |= NodeFlags.Scanned;
        }
    }

    public string GetPath(int index)
    {
        var parts = new List<string>();
        for (int i = index; i != None; i = this[i].Parent) parts.Add(GetName(i));
        parts.Reverse();
        var sb = new StringBuilder(parts[0].TrimEnd('\\'));
        for (int i = 1; i < parts.Count; i++) sb.Append('\\').Append(parts[i]);
        if (sb.Length == 2 && sb[1] == ':') sb.Append('\\');
        return sb.ToString();
    }

    public int Depth(int index)
    {
        int d = 0;
        for (int i = this[index].Parent; i != None; i = this[i].Parent) d++;
        return d;
    }

    /// <summary>Finds a direct child by name (case-insensitive), or <see cref="None"/>.</summary>
    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        for (int c = this[parent].FirstChild; c != None; c = this[c].NextSibling)
            if (NameEquals(c, name)) return c;
        return None;
    }

    /// <summary>Resolves a full path to a node, or <see cref="None"/> when it is not in the tree.</summary>
    public int FindPath(string path)
    {
        if (Root == None) return None;
        string root = RootPath.TrimEnd('\\');
        string p = path.TrimEnd('\\');
        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return None;
        if (p.Length == root.Length) return Root;
        if (p[root.Length] != '\\') return None;
        int node = Root;
        foreach (var part in p[(root.Length + 1)..].Split('\\'))
        {
            node = FindChild(node, part);
            if (node == None) return None;
        }
        return node;
    }

    /// <summary>
    /// Removes a node from its parent's child list and subtracts its sizes from all ancestors.
    /// Must not run concurrently with a scan.
    /// </summary>
    public void Remove(int index)
    {
        ref var n = ref this[index];
        int parent = n.Parent;
        if (parent == None) throw new InvalidOperationException("Cannot remove the root.");
        ref var p = ref this[parent];
        if (p.FirstChild == index) p.FirstChild = n.NextSibling;
        else
        {
            int c = p.FirstChild;
            while (c != None && this[c].NextSibling != index) c = this[c].NextSibling;
            if (c != None) this[c].NextSibling = n.NextSibling;
        }
        int files = n.IsDirectory ? n.FileCount : 1;
        int dirs = n.IsDirectory ? n.DirCount + 1 : 0;
        AddToAncestors(parent, -n.Size, -n.Length, -files, -dirs);
        n.Flags |= NodeFlags.Deleted;
        MarkDescendantsDeleted(index);
        MarkChanged();
    }

    /// <summary>
    /// Drops a directory's contents and zeroes its totals so it can be scanned again.
    /// Must not run concurrently with a scan.
    /// </summary>
    public void ClearChildren(int dir)
    {
        ref var n = ref this[dir];
        MarkDescendantsDeleted(dir);
        AddToAncestors(dir, -n.Size, -n.Length, -n.FileCount, -n.DirCount);
        n.FirstChild = None;
        n.Flags &= ~(NodeFlags.Scanned | NodeFlags.Error);
        MarkChanged();
    }

    /// <summary>
    /// Marks as deleted every node that cannot be reached from the root. Raw MFT data can contain parent cycles
    /// (A inside B inside A); such nodes are not in the tree, and walking up from them would never end.
    /// Returns how many were dropped.
    /// </summary>
    internal int DropUnreachable()
    {
        var reached = new bool[Count];
        var stack = new Stack<int>();
        stack.Push(Root);
        reached[Root] = true;
        while (stack.Count > 0)
            for (int c = this[stack.Pop()].FirstChild; c != None; c = this[c].NextSibling)
                if (!reached[c])
                {
                    reached[c] = true;
                    stack.Push(c);
                }
        int dropped = 0;
        for (int i = 0; i < reached.Length; i++)
            if (!reached[i] && (this[i].Flags & (NodeFlags.Used | NodeFlags.Deleted)) == NodeFlags.Used)
            {
                this[i].Flags |= NodeFlags.Deleted;
                dropped++;
            }
        return dropped;
    }

    /// <summary>Discards everything but the root, including nodes not yet linked into the tree.</summary>
    internal void ResetToRoot()
    {
        for (int i = 0; i < Count; i++)
            if (i != Root) this[i].Flags |= NodeFlags.Deleted;
        ref var r = ref this[Root];
        r.FirstChild = None;
        r.Size = r.Length = 0;
        r.FileCount = r.DirCount = 0;
        r.Flags &= ~(NodeFlags.Scanned | NodeFlags.Error);
        MarkChanged();
    }

    void MarkDescendantsDeleted(int index)
    {
        var stack = new Stack<int>();
        stack.Push(index);
        while (stack.Count > 0)
            for (int c = this[stack.Pop()].FirstChild; c != None; c = this[c].NextSibling)
            {
                this[c].Flags |= NodeFlags.Deleted;
                if (this[c].IsDirectory) stack.Push(c);
            }
    }

    /// <summary>True when the node is in the tree (neither it nor an ancestor has been removed).</summary>
    public bool IsLive(int index) => (this[index].Flags & (NodeFlags.Used | NodeFlags.Deleted)) == NodeFlags.Used;

    int Allocate()
    {
        int i = Interlocked.Increment(ref _count) - 1;
        if (_nodes[i >> NodeShift] == null) Grow(ref _nodes[i >> NodeShift], NodeChunk);
        return i;
    }

    void Grow<T>(ref T[]? slot, int size)
    {
        lock (_growLock)
        {
            if (Volatile.Read(ref slot) == null) Volatile.Write(ref slot, new T[size]);
        }
    }

    uint AddName(ReadOnlySpan<char> name, out ushort encodedLen)
    {
        bool ascii = true;
        foreach (char c in name)
            if (c > 0xFF) { ascii = false; break; }
        int bytes = ascii ? name.Length : name.Length * 2;
        if (bytes >= Utf16Bit) throw new ArgumentException("Name too long.", nameof(name));

        uint offset = ReserveName(bytes, out var dest);
        if (ascii)
            for (int i = 0; i < name.Length; i++) dest[i] = (byte)name[i];
        else
            MemoryMarshal.AsBytes(name).CopyTo(dest);
        encodedLen = (ushort)(ascii ? bytes : bytes | Utf16Bit);
        return offset;
    }

    uint ReserveName(int bytes, out Span<byte> dest)
    {
        long start;
        while (true)
        {
            long old = Volatile.Read(ref _namePos);
            start = old;
            if ((start & (NameChunk - 1)) + bytes > NameChunk) start = (start | (NameChunk - 1)) + 1;
            if (Interlocked.CompareExchange(ref _namePos, start + bytes, old) == old) break;
        }
        int chunk = (int)(start >> NameShift);
        if (chunk >= _names.Length) throw new OutOfMemoryException("Name storage exhausted.");
        if (_names[chunk] == null) Grow(ref _names[chunk], NameChunk);
        dest = new Span<byte>(_names[chunk], (int)(start & (NameChunk - 1)), bytes);
        return (uint)start;
    }
}
