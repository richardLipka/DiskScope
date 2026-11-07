using System.Runtime.InteropServices;

namespace DiskScope.Core.Deletion;

public enum DeleteMethod
{
    RecycleBin,
    Permanent,
}

/// <summary>Outcome of deleting one item: whether it is gone, and how many entries inside could not be deleted.</summary>
public readonly record struct DeleteResult(bool Gone, int FailedEntries, string? Error);

public interface IFileDeleter
{
    DeleteResult Delete(string path, bool isDirectory, DeleteMethod method, CancellationToken cancel);
}

/// <summary>
/// Deletes without any shell dialogs. Permanent deletion walks the tree itself so that one locked file does not stop
/// the rest, and never follows junctions or symbolic links (only the link is removed, not what it points to).
/// </summary>
public sealed class FileDeleter : IFileDeleter
{
    /// <summary>Window that owns the only dialog the shell may still show: an item too large for the recycle bin.</summary>
    public nint OwnerWindow { get; set; }

    public DeleteResult Delete(string path, bool isDirectory, DeleteMethod method, CancellationToken cancel)
    {
        // A disconnected or failing disk: report it rather than try (and rather than mistake it for "gone").
        if (Probe(path) is { State: PathState.Unavailable } before)
            return new DeleteResult(false, 0, before.Error);

        int failed = 0;
        string? error = null;
        try
        {
            if (method == DeleteMethod.RecycleBin) error = Recycle(path);
            else DeleteTree(isDirectory || Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path), ref failed, ref error, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        // Only "not found" counts as gone; any other answer (device error, not ready, denied) means it may still be there.
        var after = Probe(path);
        bool gone = after.State == PathState.Missing;
        return new DeleteResult(gone, failed, gone ? null : error ?? after.Error ?? "?");
    }

    enum PathState
    {
        Exists,
        Missing,
        Unavailable,
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFileAttributes(string path);

    /// <summary>
    /// Whether a path exists, is missing, or cannot be told (File.Exists answers "no" to all of a disk error,
    /// an unplugged drive and access denied, which would make a failed delete look like a success).
    /// </summary>
    static (PathState State, string? Error) Probe(string path)
    {
        const uint Invalid = 0xFFFFFFFF;
        const int FileNotFound = 2, PathNotFound = 3;
        string full = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + Path.GetFullPath(path);
        if (GetFileAttributes(full) != Invalid) return (PathState.Exists, null);
        int code = Marshal.GetLastPInvokeError();
        if (code is FileNotFound or PathNotFound)
        {
            // "Path not found" is also what a vanished drive says: check that the volume itself is still there.
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root != null && GetFileAttributes(root.StartsWith(@"\\", StringComparison.Ordinal) ? root : @"\\?\" + root) == Invalid)
                return (PathState.Unavailable, new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return (PathState.Missing, null);
        }
        return (PathState.Unavailable, new System.ComponentModel.Win32Exception(code).Message);
    }

    static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>
    /// Deletes a folder's contents bottom-up, then the folder. Iterative, so an extremely deep folder tree cannot
    /// overflow the stack (which would end the process, as a stack overflow cannot be caught).
    /// </summary>
    static void DeleteTree(FileSystemInfo root, ref int failed, ref string? error, CancellationToken cancel)
    {
        var stack = new Stack<(FileSystemInfo Item, bool ContentsDone)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (item, contentsDone) = stack.Pop();
            FileAttributes attributes;
            try
            {
                attributes = item.Attributes;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                failed++;
                error ??= ex.Message;
                continue;
            }
            if ((int)attributes == -1) continue; // vanished meanwhile

            bool link = (attributes & FileAttributes.ReparsePoint) != 0;
            if (item is DirectoryInfo dir && !link && !contentsDone)
            {
                List<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos().ToList();
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    failed++;
                    error ??= ex.Message;
                    continue;
                }
                stack.Push((item, true)); // the folder itself, once its entries are done
                foreach (var entry in entries) stack.Push((entry, false));
                continue;
            }

            try
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) item.Attributes = attributes & ~FileAttributes.ReadOnly;
                // For a junction or symlink this removes only the link itself.
                if (item is DirectoryInfo d) d.Delete(recursive: false);
                else item.Delete();
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                failed++;
                error ??= ex.Message;
            }
        }
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4;
    const ushort FOF_NOCONFIRMATION = 0x10;
    const ushort FOF_ALLOWUNDO = 0x40;
    const ushort FOF_NOERRORUI = 0x400;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    string? Recycle(string path)
    {
        // Silent, except that the shell still asks before deleting for good an item the recycle bin cannot take.
        var op = new SHFILEOPSTRUCT
        {
            hwnd = OwnerWindow,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int code = SHFileOperation(ref op);
        return code == 0 && !op.fAnyOperationsAborted ? null : op.fAnyOperationsAborted ? "Cancelled" : $"Shell error 0x{code:X}";
    }
}
