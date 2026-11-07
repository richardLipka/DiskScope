using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskScope.Core.Native;
using static DiskScope.Core.Native.NativeMethods;

namespace DiskScope.Core.Scanning;

/// <summary>
/// Works on any file system: lists directories in parallel with NtQueryDirectoryFile,
/// which returns the allocated size of every entry without opening the files.
/// </summary>
public sealed unsafe class DirectoryScanner : IScanner
{
    const int BufferSize = 256 * 1024;

    readonly int _threads;

    public DirectoryScanner(int threads = 0)
    {
        _threads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount, 4, 16);
    }

    public ScanMode Mode => ScanMode.Directory;

    public void Scan(NodeStore store, string rootPath, ScanProgress progress, CancellationToken cancel)
    {
        new Run(store, progress, cancel, _threads).Execute(store.Root, rootPath);
    }

    /// <summary>Scans a directory again: its old contents are dropped and re-listed.</summary>
    public void Rescan(NodeStore store, int dir, ScanProgress progress, CancellationToken cancel)
    {
        store.ClearChildren(dir);
        new Run(store, progress, cancel, _threads).Execute(dir, store.GetPath(dir));
    }

    readonly record struct WorkItem(int Node, string Path);

    sealed class Run(NodeStore store, ScanProgress progress, CancellationToken cancel, int threads)
    {
        readonly ConcurrentStack<WorkItem> _work = new();
        readonly SemaphoreSlim _available = new(0);
        int _pending;
        volatile bool _finished;

        public void Execute(int root, string rootPath)
        {
            Push(new WorkItem(root, rootPath));
            var workers = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                workers[i] = new Thread(Worker) { IsBackground = true, Name = "DirectoryScanner" };
                workers[i].Start();
            }
            foreach (var w in workers) w.Join();
            cancel.ThrowIfCancellationRequested();
        }

        void Push(WorkItem item)
        {
            Interlocked.Increment(ref _pending);
            _work.Push(item);
            _available.Release();
        }

        void Worker()
        {
            var buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
            try
            {
                while (true)
                {
                    _available.Wait();
                    if (_finished) return;
                    if (!_work.TryPop(out var item)) continue;
                    if (!cancel.IsCancellationRequested)
                    {
                        try
                        {
                            ListDirectory(item, buffer);
                        }
                        catch (Exception)
                        {
                            // One unreadable folder must not end the scan (or the process): it is shown as an error.
                            store.SetFlag(item.Node, NodeFlags.Error | NodeFlags.Scanned);
                            Interlocked.Increment(ref progress.Errors);
                        }
                    }
                    if (Interlocked.Decrement(ref _pending) == 0)
                    {
                        _finished = true;
                        _available.Release(threads);
                    }
                }
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }

        void ListDirectory(WorkItem item, byte* buffer)
        {
            progress.CurrentPath = item.Path;
            string openPath = item.Path.StartsWith(@"\\", StringComparison.Ordinal) ? item.Path : @"\\?\" + item.Path;
            using var handle = CreateFile(openPath, FILE_LIST_DIRECTORY, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (handle.IsInvalid)
            {
                store.SetFlag(item.Node, NodeFlags.Error | NodeFlags.Scanned);
                Interlocked.Increment(ref progress.Errors);
                return;
            }

            int head = NodeStore.None, tail = NodeStore.None;
            long size = 0, length = 0, newestWrite = 0, newestAccess = 0;
            int files = 0, dirs = 0;
            bool restart = true;
            var subdirs = new List<WorkItem>();
            string prefix = item.Path.EndsWith('\\') ? item.Path : item.Path + "\\";

            while (true)
            {
                int status = NtQueryDirectoryFile(handle, 0, 0, 0, out _, buffer, BufferSize, FileFullDirectoryInformation, false, 0, restart);
                restart = false;
                if (status == STATUS_NO_MORE_FILES) break;
                if (status < 0)
                {
                    store.SetFlag(item.Node, NodeFlags.Error);
                    Interlocked.Increment(ref progress.Errors);
                    break;
                }

                byte* entry = buffer;
                while (true)
                {
                    // FILE_FULL_DIR_INFORMATION
                    uint next = *(uint*)entry;
                    long atime = *(long*)(entry + 16);
                    long mtime = *(long*)(entry + 24);
                    long eof = *(long*)(entry + 40);
                    long alloc = *(long*)(entry + 48);
                    uint attrs = *(uint*)(entry + 56);
                    int nameLen = (int)(*(uint*)(entry + 60) / 2);
                    uint reparseTag = *(uint*)(entry + 64);
                    var name = new ReadOnlySpan<char>(entry + 68, nameLen);

                    if (!(name is "." or ".."))
                    {
                        bool isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
                        NodeFlags flags = isDir ? NodeFlags.Directory : NodeFlags.None;
                        bool follow = isDir;
                        if (isDir && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
                            reparseTag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK)
                        {
                            flags |= NodeFlags.Reparse | NodeFlags.Scanned;
                            follow = false;
                        }

                        // A directory's own access time is left out: listing it (as this scan does) updates it.
                        int node = store.Add(item.Node, name, flags, attrs, isDir ? 0 : alloc, isDir ? 0 : eof, mtime, isDir ? 0 : atime);
                        if (tail == NodeStore.None) head = node; else store[tail].NextSibling = node;
                        tail = node;
                        newestWrite = Math.Max(newestWrite, mtime);
                        if (!isDir) newestAccess = Math.Max(newestAccess, atime);

                        if (isDir)
                        {
                            dirs++;
                            if (follow) subdirs.Add(new WorkItem(node, prefix + name.ToString()));
                        }
                        else
                        {
                            files++;
                            size += alloc;
                            length += eof;
                        }
                    }
                    if (next == 0) break;
                    entry += next;
                }
            }

            if (head != NodeStore.None) store.PublishChildren(item.Node, head);
            store.AddToAncestors(item.Node, size, length, files, dirs);
            store.MaxTimesToAncestors(item.Node, newestWrite, newestAccess);
            store.SetFlag(item.Node, NodeFlags.Scanned);
            Interlocked.Add(ref progress.Files, files);
            Interlocked.Add(ref progress.Directories, dirs);
            Interlocked.Add(ref progress.Bytes, size);
            for (int i = subdirs.Count - 1; i >= 0; i--) Push(subdirs[i]);
        }
    }
}
