using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskScope.Core.Native;

/// <summary>
/// NTFS junctions (directory mount points): a folder that leads to another folder. Unlike symbolic links they need no
/// special privilege, so DiskScope can leave one behind when it moves a folder to another drive.
/// </summary>
public static class Junction
{
    const uint GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize,
        out int returned, nint overlapped);

    /// <summary>Creates a junction at <paramref name="link"/> (which must not exist) leading to the folder <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        string targetPath = Plain(Path.GetFullPath(LongPath.Of(target)));
        Directory.CreateDirectory(LongPath.Of(link));
        try
        {
            using var handle = CreateFileW(LongPath.Of(link), GENERIC_WRITE, 0, 0, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var buffer = MountPointData(@"\??\" + targetPath, targetPath);
            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            try { Directory.Delete(LongPath.Of(link)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Where a junction or symbolic link leads, or null when the path is not a link.</summary>
    public static string? Target(string path) =>
        new DirectoryInfo(LongPath.Of(path)) is { Exists: true, LinkTarget: string t } ? Plain(t) : null;

    static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>REPARSE_DATA_BUFFER for a mount point: header, then the substitute and print names, each NUL-terminated.</summary>
    static byte[] MountPointData(string substitute, string print)
    {
        byte[] sub = Encoding.Unicode.GetBytes(substitute);
        byte[] prn = Encoding.Unicode.GetBytes(print);
        int pathBytes = sub.Length + 2 + prn.Length + 2;
        var buffer = new byte[8 + 8 + pathBytes];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, IO_REPARSE_TAG_MOUNT_POINT);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(8 + pathBytes)); // ReparseDataLength
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);                          // SubstituteNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)sub.Length);        // SubstituteNameLength
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(sub.Length + 2));  // PrintNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)prn.Length);        // PrintNameLength
        sub.CopyTo(span[16..]);
        prn.CopyTo(span[(16 + sub.Length + 2)..]);
        return buffer;
    }
}
