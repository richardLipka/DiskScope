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
        int failed = 0;
        string? error = null;
        try
        {
            if (method == DeleteMethod.RecycleBin) error = Recycle(path);
            else DeleteTree(new DirectoryInfo(path).Exists ? new DirectoryInfo(path) : new FileInfo(path), ref failed, ref error, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        bool gone = !File.Exists(path) && !Directory.Exists(path);
        return new DeleteResult(gone, failed, gone ? null : error ?? "?");
    }

    static void DeleteTree(FileSystemInfo item, ref int failed, ref string? error, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        bool link = (item.Attributes & FileAttributes.ReparsePoint) != 0;
        if (item is DirectoryInfo dir && !link)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                error ??= ex.Message;
                return;
            }
            foreach (var entry in entries) DeleteTree(entry, ref failed, ref error, cancel);
        }
        try
        {
            if ((item.Attributes & FileAttributes.ReadOnly) != 0) item.Attributes &= ~FileAttributes.ReadOnly;
            // For a junction or symlink this removes only the link itself.
            if (item is DirectoryInfo d) d.Delete(recursive: false);
            else item.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed++;
            error ??= ex.Message;
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
