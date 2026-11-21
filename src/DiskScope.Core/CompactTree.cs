using System.Runtime.InteropServices;
using System.Text;

namespace DiskScope.Core;

/// <summary>
/// A read-only tree kept as small as possible, for the older scan in a comparison. Nodes are stored in preorder,
/// each with its on-disk size, the number of nodes in its subtree and its name: about 19 bytes per entry plus the
/// name, against 64 for a <see cref="NodeStore"/> node. The first child of node i is i + 1, and the next sibling of
/// child c is c + Span(c), so no links are stored at all.
/// </summary>
public sealed class CompactTree
{
    public const int Root = 0;
    const int NameShift = 22;
    const int NameChunk = 1 << NameShift;
    const ushort Utf16Bit = 0x8000;
    const int DirBit = int.MinValue;

    long[] _size;
    int[] _span; // subtree node count, with DirBit for directories
    uint[] _nameOffset;
    ushort[] _nameLen;
    readonly List<byte[]> _names = [];
    long _namePos;
    int _count;

    internal CompactTree(long capacity)
    {
        int cap = (int)Math.Clamp(capacity, 16, 64_000_000); // a damaged header must not reserve gigabytes up front
        _size = new long[cap];
        _span = new int[cap];
        _nameOffset = new uint[cap];
        _nameLen = new ushort[cap];
    }

    public int Count => _count;

    public string RootPath => _count == 0 ? "" : GetName(Root);

    /// <summary>Approximate memory taken, for tests and diagnostics.</summary>
    public long MemoryUsage => (long)_size.Length * (8 + 4 + 4 + 2) + (long)_names.Count * NameChunk;

    public bool IsDirectory(int node) => (_span[node] & DirBit) != 0;

    public long Size(int node) => _size[node];

    /// <summary>Number of nodes in the subtree of <paramref name="node"/>, itself included.</summary>
    public int Span(int node) => _span[node] & int.MaxValue;

    public IEnumerable<int> Children(int dir)
    {
        if (!IsDirectory(dir)) yield break;
        int end = dir + Span(dir);
        for (int c = dir + 1; c < end; c += Span(c)) yield return c;
    }

    public int FindChild(int dir, ReadOnlySpan<char> name)
    {
        if (!IsDirectory(dir)) return NodeStore.None;
        int end = dir + Span(dir);
        for (int c = dir + 1; c < end; c += Span(c))
            if (NameEquals(c, name)) return c;
        return NodeStore.None;
    }

    /// <summary>Resolves a full path, as <see cref="NodeStore.FindPath"/> does; the root may be a folder above it.</summary>
    public int FindPath(string path)
    {
        if (_count == 0) return NodeStore.None;
        string root = RootPath.TrimEnd('\\');
        string p = path.TrimEnd('\\');
        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return NodeStore.None;
        if (p.Length == root.Length) return Root;
        if (p[root.Length] != '\\') return NodeStore.None;
        int node = Root;
        foreach (var part in p[(root.Length + 1)..].Split('\\'))
        {
            node = FindChild(node, part);
            if (node == NodeStore.None) return NodeStore.None;
        }
        return node;
    }

    public string GetName(int node)
    {
        var bytes = NameBytes(node);
        return (_nameLen[node] & Utf16Bit) != 0 ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.Latin1.GetString(bytes);
    }

    public bool NameEquals(int node, ReadOnlySpan<char> name)
    {
        var bytes = NameBytes(node);
        if ((_nameLen[node] & Utf16Bit) != 0)
            return MemoryMarshal.Cast<byte, char>(bytes).Equals(name, StringComparison.OrdinalIgnoreCase);
        if (bytes.Length != name.Length) return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            char a = (char)bytes[i], b = name[i];
            if (a != b && char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
        }
        return true;
    }

    ReadOnlySpan<byte> NameBytes(int node)
    {
        uint offset = _nameOffset[node];
        return new ReadOnlySpan<byte>(_names[(int)(offset >> NameShift)], (int)(offset & (NameChunk - 1)), _nameLen[node] & ~Utf16Bit);
    }

    // --- building (single-threaded, in preorder) ---

    /// <summary>Appends the next node in preorder. A directory's size and span are set by <see cref="Close"/>.</summary>
    internal int Add(ReadOnlySpan<byte> rawName, ushort encodedLen, bool isDirectory, long size)
    {
        if (_count == _size.Length) Grow();
        int i = _count++;
        _size[i] = size;
        _span[i] = isDirectory ? DirBit | 1 : 1;
        _nameLen[i] = encodedLen;
        _nameOffset[i] = StoreName(rawName);
        return i;
    }

    /// <summary>Ends a directory: everything added since it belongs to its subtree.</summary>
    internal void Close(int dir, long size)
    {
        _span[dir] = DirBit | (_count - dir);
        _size[dir] = size;
    }

    void Grow()
    {
        if (_count >= int.MaxValue / 2) throw new OutOfMemoryException("Tree too large.");
        int cap = _size.Length * 2;
        Array.Resize(ref _size, cap);
        Array.Resize(ref _span, cap);
        Array.Resize(ref _nameOffset, cap);
        Array.Resize(ref _nameLen, cap);
    }

    uint StoreName(ReadOnlySpan<byte> raw)
    {
        long start = _namePos;
        if ((start & (NameChunk - 1)) + raw.Length > NameChunk) start = (start | (NameChunk - 1)) + 1;
        if (start + raw.Length > uint.MaxValue) throw new OutOfMemoryException("Name storage exhausted.");
        int chunk = (int)(start >> NameShift);
        while (_names.Count <= chunk) _names.Add(new byte[NameChunk]);
        raw.CopyTo(_names[chunk].AsSpan((int)(start & (NameChunk - 1))));
        _namePos = start + raw.Length;
        return (uint)start;
    }

    /// <summary>A compact copy of the live part of a tree (used by tests and when a full tree is at hand).</summary>
    public static CompactTree From(NodeStore store)
    {
        var tree = new CompactTree(store.Count);
        if (store.Root == NodeStore.None) return tree;
        // Preorder with an explicit stack; a directory is closed once all of its subtree has been added.
        var stack = new Stack<(int Node, int Index, bool Closing)>();
        stack.Push((store.Root, -1, false));
        var children = new List<int>();
        while (stack.Count > 0)
        {
            var (node, index, closing) = stack.Pop();
            if (closing)
            {
                tree.Close(index, store[node].Size);
                continue;
            }
            var raw = store.RawName(node, out ushort len);
            bool dir = store[node].IsDirectory;
            int i = tree.Add(raw, len, dir, store[node].Size);
            if (!dir) continue;
            stack.Push((node, i, true));
            children.Clear();
            for (int c = store[node].FirstChild; c != NodeStore.None; c = store[c].NextSibling) children.Add(c);
            for (int k = children.Count - 1; k >= 0; k--) stack.Push((children[k], -1, false));
        }
        return tree;
    }
}
