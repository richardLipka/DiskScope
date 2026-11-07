using static DiskScope.Core.Native.NativeMethods;

namespace DiskScope.Core.Scanning.Mft;

/// <summary>
/// Turns parsed MFT records into the tree. Records arrive in MFT order, so a file may appear before
/// its directory: nodes first remember their parent's record number, and are linked in <see cref="Finish"/>.
/// </summary>
internal sealed class MftBuilder
{
    readonly NodeStore _store;
    readonly ScanProgress _progress;
    readonly int[] _recordToNode;
    readonly List<(uint Parent, int Start, int Length)> _names = [];
    char[] _nameChars = new char[4096];
    int _nameUsed;

    // Rare cases: data or names stored in extension records, and base records whose names all live there.
    readonly List<Extension> _extensions = [];
    readonly Dictionary<uint, Nameless> _nameless = [];

    sealed record Extension(uint BaseRecord, List<(uint Parent, string Name)> Names, long OnDisk, long Length);
    record struct Nameless(NodeFlags Flags, uint Attributes, long MTime, long ATime, long OnDisk, long Length);

    public MftBuilder(NodeStore store, ScanProgress progress, long totalRecords)
    {
        _store = store;
        _progress = progress;
        _recordToNode = new int[totalRecords];
        Array.Fill(_recordToNode, NodeStore.None);
        _recordToNode[MftParser.RootRecord] = store.Root;
    }

    public void AddRecord(ReadOnlySpan<byte> rec, uint number)
    {
        _names.Clear();
        _nameUsed = 0;
        if (number >= _recordToNode.Length || !MftParser.Parse(rec, number, out var r, CollectName)) return;

        if (r.IsExtension)
        {
            if (_names.Count > 0 || r.OnDisk != 0 || r.Length >= 0)
                _extensions.Add(new Extension(r.BaseRecord, _names.Select(n => (n.Parent, NameAt(n).ToString())).ToList(), r.OnDisk, r.Length));
            return;
        }
        if (number == MftParser.RootRecord)
        {
            _store[_store.Root].MTime = r.MTime;
            return;
        }

        var flags = r.IsDirectory ? NodeFlags.Directory : NodeFlags.None;
        if (r.IsDirectory && r.ReparseTag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK) flags |= NodeFlags.Reparse;
        long length = Math.Max(0, r.Length);
        if (_names.Count == 0)
        {
            _nameless[number] = new Nameless(flags, r.Attributes, r.MTime, r.ATime, r.OnDisk, length);
            return;
        }
        for (int i = 0; i < _names.Count; i++)
        {
            var n = _names[i];
            bool primary = i == 0;
            int node = AddNode(n.Parent, NameAt(n), primary ? flags : flags | NodeFlags.HardLink, r.Attributes,
                primary ? r.OnDisk : 0, primary ? length : 0, r.MTime, r.ATime);
            if (primary) _recordToNode[number] = node;
        }
    }

    void CollectName(in MftName name)
    {
        if (_nameUsed + name.Name.Length > _nameChars.Length) Array.Resize(ref _nameChars, _nameChars.Length * 2 + name.Name.Length);
        name.Name.CopyTo(_nameChars.AsSpan(_nameUsed));
        _names.Add((name.ParentRecord, _nameUsed, name.Name.Length));
        _nameUsed += name.Name.Length;
    }

    ReadOnlySpan<char> NameAt((uint Parent, int Start, int Length) n) => _nameChars.AsSpan(n.Start, n.Length);

    int AddNode(uint parentRecord, ReadOnlySpan<char> name, NodeFlags flags, uint attributes, long onDisk, long length, long mtime, long atime)
    {
        bool dir = (flags & NodeFlags.Directory) != 0;
        // Parent holds the parent's record number until Finish links the tree.
        // A directory's own access time is left out: merely listing it updates it.
        int node = _store.Add((int)parentRecord, name, flags, attributes, dir ? 0 : onDisk, dir ? 0 : length, mtime, dir ? 0 : atime);
        if (dir) _progress.Directories++;
        else if ((flags & NodeFlags.HardLink) == 0)
        {
            _progress.Files++;
            _progress.Bytes += onDisk;
        }
        return node;
    }

    public void Finish()
    {
        ApplyExtensions();
        Link();
        _store.ComputeTotalsFromRoot();
        _store.MarkChanged();
    }

    void ApplyExtensions()
    {
        foreach (var ext in _extensions)
        {
            int primary = ext.BaseRecord < _recordToNode.Length ? _recordToNode[ext.BaseRecord] : NodeStore.None;
            if (primary == NodeStore.None && _nameless.TryGetValue(ext.BaseRecord, out var nl))
            {
                if (ext.Names.Count == 0)
                {
                    _nameless[ext.BaseRecord] = nl with { OnDisk = nl.OnDisk + ext.OnDisk, Length = ext.Length >= 0 ? ext.Length : nl.Length };
                    continue;
                }
                var (parent, name) = ext.Names[0];
                primary = AddNode(parent, name, nl.Flags, nl.Attributes, nl.OnDisk, nl.Length, nl.MTime, nl.ATime);
                _recordToNode[ext.BaseRecord] = primary;
                _nameless.Remove(ext.BaseRecord);
                ext.Names.RemoveAt(0);
            }
            if (primary == NodeStore.None) continue;

            ref var p = ref _store[primary];
            foreach (var (parent, name) in ext.Names)
                AddNode(parent, name, p.Flags | NodeFlags.HardLink, p.Attributes, 0, 0, p.MTime, p.ATime);
            if (!p.IsDirectory)
            {
                p.Size += ext.OnDisk;
                if (ext.Length >= 0) p.Length = ext.Length;
                _progress.Bytes += ext.OnDisk;
            }
        }
    }

    void Link()
    {
        int root = _store.Root;
        int count = _store.Count;
        for (int i = 0; i < count; i++)
        {
            if (i == root) continue;
            ref var n = ref _store[i];
            if ((n.Flags & NodeFlags.Used) == 0) continue;
            int record = n.Parent;
            int parent = record >= 0 && record < _recordToNode.Length ? _recordToNode[record] : NodeStore.None;
            // Orphans (parent record missing or not a directory) go under the root rather than vanish.
            if (parent == NodeStore.None || parent == i || !_store[parent].IsDirectory) parent = root;
            n.Parent = parent;
            n.NextSibling = _store[parent].FirstChild;
            _store[parent].FirstChild = i;
        }
    }
}
