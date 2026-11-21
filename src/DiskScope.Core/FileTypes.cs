namespace DiskScope.Core;

/// <summary>Broad groups of file types, for coloring the treemap and grouping the file types view.</summary>
public enum FileKind
{
    Video,
    Image,
    Audio,
    Archive,
    DiskImage,
    Program,
    Document,
    Code,
    Other,
}

/// <summary>Total size and count of one file extension inside a folder.</summary>
public sealed record FileTypeEntry(string Extension, FileKind Kind, long Size, long Count);

/// <summary>What kinds of files fill a folder: totals per extension, and the largest files of one extension.</summary>
public static class FileTypes
{
    static readonly Dictionary<string, FileKind> Kinds = BuildKinds();

    public static FileKind KindOf(string name) => KindOfExtension(ExtensionOf(name));

    public static FileKind KindOfExtension(ReadOnlySpan<char> extension) =>
        Kinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension, out var kind) ? kind : FileKind.Other;

    /// <summary>
    /// The extension in lower case with its dot (".iso"), or "" for none. A "dot part" that is long or contains a
    /// space ("report.final version") is not an extension, and neither is the dot of a name like ".gitignore".
    /// </summary>
    public static string ExtensionOf(ReadOnlySpan<char> name)
    {
        var ext = ExtensionSpan(name);
        return ext.IsEmpty ? "" : ext.ToString().ToLowerInvariant();
    }

    static ReadOnlySpan<char> ExtensionSpan(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > 16) return [];
        var ext = name[dot..];
        return ext.Contains(' ') ? [] : ext;
    }

    /// <summary>Totals per extension of all files under <paramref name="folder"/>, largest first.</summary>
    public static List<FileTypeEntry> Totals(NodeStore store, int folder, CancellationToken cancel = default)
    {
        var totals = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        var lookup = totals.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> nameBuf = stackalloc char[32768];
        var stack = new Stack<int>();
        stack.Push(folder);
        long visited = 0;
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            ref var e = ref store[n];
            if (e.IsDirectory)
            {
                for (int c = Volatile.Read(ref e.FirstChild); c != NodeStore.None; c = store[c].NextSibling) stack.Push(c);
                continue;
            }
            if ((++visited & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            var ext = ExtensionSpan(store.CopyName(n, nameBuf));
            // Extensions are few; looking them up by span allocates only for the first file of each.
            if (lookup.TryGetValue(ext, out var t)) lookup[ext] = (t.Size + e.Size, t.Count + 1);
            else totals[ext.ToString().ToLowerInvariant()] = (e.Size, 1);
        }
        return totals.Select(x => new FileTypeEntry(x.Key, KindOfExtension(x.Key), x.Value.Size, x.Value.Count))
            .OrderByDescending(x => x.Size).ThenBy(x => x.Extension, StringComparer.Ordinal).ToList();
    }

    /// <summary>The largest files with the given extension ("" for none) under <paramref name="folder"/>, largest first.</summary>
    public static List<int> LargestFiles(NodeStore store, int folder, string extension, int limit, CancellationToken cancel = default)
    {
        var heap = new PriorityQueue<int, long>(limit + 1);
        Span<char> nameBuf = stackalloc char[32768];
        var stack = new Stack<int>();
        stack.Push(folder);
        long visited = 0;
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            ref var e = ref store[n];
            if (e.IsDirectory)
            {
                for (int c = Volatile.Read(ref e.FirstChild); c != NodeStore.None; c = store[c].NextSibling) stack.Push(c);
                continue;
            }
            if ((++visited & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            if (!ExtensionSpan(store.CopyName(n, nameBuf)).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
            long size = e.Size;
            if (heap.Count < limit) heap.Enqueue(n, size);
            else if (limit > 0 && heap.TryPeek(out _, out long smallest) && size > smallest) heap.EnqueueDequeue(n, size);
        }
        var result = new List<int>(heap.Count);
        while (heap.TryDequeue(out int node, out _)) result.Add(node);
        result.Reverse();
        return result;
    }

    static Dictionary<string, FileKind> BuildKinds()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string exts)
        {
            foreach (var e in exts.Split(' ')) map["." + e] = kind;
        }
        Add(FileKind.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts vob 3gp");
        Add(FileKind.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico");
        Add(FileKind.Audio, "mp3 wav flac aac ogg m4a wma opus aiff mid");
        Add(FileKind.Archive, "zip rar 7z gz tgz bz2 xz zst tar cab lz4 pak");
        Add(FileKind.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 wim esd dmg bin");
        Add(FileKind.Program, "exe dll sys msi msix appx ocx drv so lib pyd node jar");
        Add(FileKind.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md epub csv");
        Add(FileKind.Code, "cs js ts py java c cpp h hpp go rs php rb json xml yml yaml html css sql pyc class");
        return map;
    }
}
