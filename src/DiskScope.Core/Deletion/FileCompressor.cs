using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskScope.Core.Deletion;

/// <summary>Compression algorithms of Windows' file compression (the same values as compact.exe /exe:…).</summary>
public enum CompressionKind
{
    Xpress4K = 0,
    Lzx = 1,
    Xpress8K = 2,
    Xpress16K = 3,
}

/// <summary>Outcome of compressing a file or folder: on-disk size before and after, and files that could not be compressed.</summary>
public readonly record struct CompressResult(long Before, long After, int Files, int Failed, string? Error)
{
    public long Saved => Math.Max(0, Before - After);
}

/// <summary>
/// Compresses files in place with Windows' own file compression (WOF, as <c>compact /exe</c> does). Files stay where
/// they are and open as usual; reading them costs a little CPU. Best for files that are read but rarely changed,
/// such as installed programs, games or old projects: a file that is written to is stored uncompressed again.
/// NTFS only.
/// </summary>
public static class FileCompressor
{
    /// <summary><see cref="CompressResult.Error"/> when the drive is not NTFS; the UI translates it.</summary>
    public const string ErrorNotNtfs = "#not-ntfs";

    /// <summary>Smaller files gain nothing (a cluster is the least a file takes).</summary>
    const long MinFileSize = 8 * 1024;

    const uint FSCTL_SET_EXTERNAL_BACKING = 0x0009030C;
    const int ERROR_COMPRESSION_NOT_BENEFICIAL = 344;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize,
        out int returned, nint overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string name, out uint high);

    /// <summary>Space a file takes on disk, compression included.</summary>
    public static long OnDiskSize(string path)
    {
        uint low = GetCompressedFileSizeW(LongPath.Of(path), out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastPInvokeError() != 0) return new FileInfo(LongPath.Of(path)).Length;
        return ((long)high << 32) | low;
    }

    public static CompressResult Compress(string path, bool isDirectory, CompressionKind kind, Action<long>? progress, CancellationToken cancel)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        try
        {
            if (!string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return new CompressResult(0, 0, 0, 0, ErrorNotNtfs);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new CompressResult(0, 0, 0, 0, ex.Message);
        }

        long before = 0, after = 0, done = 0;
        int files = 0, failed = 0;
        string? error = null;
        foreach (string file in Files(LongPath.Of(path), isDirectory || Directory.Exists(LongPath.Of(path))))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                long length = new FileInfo(file).Length;
                long sizeBefore = OnDiskSize(file);
                before += sizeBefore;
                if (length >= MinFileSize && CompressFile(file, kind) is int code && code != 0)
                {
                    failed++;
                    error ??= new Win32Exception(code).Message;
                }
                after += OnDiskSize(file);
                files++;
                done += length;
                progress?.Invoke(done);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                error ??= ex.Message;
            }
        }
        return new CompressResult(before, after, files, failed, error);
    }

    /// <summary>Compresses one file; 0 on success or when it does not compress, otherwise the Windows error code.</summary>
    static int CompressFile(string file, CompressionKind kind)
    {
        // Files that are open for writing elsewhere cannot be compressed; they are counted as failed.
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
        // WOF_EXTERNAL_INFO { Version = 1, Provider = WOF_PROVIDER_FILE (2) } + FILE_PROVIDER_EXTERNAL_INFO_V1 { Version = 1, Algorithm, Flags = 0 }
        var input = new byte[20];
        BitConverter.TryWriteBytes(input.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(input.AsSpan(4), 2u);
        BitConverter.TryWriteBytes(input.AsSpan(8), 1u);
        BitConverter.TryWriteBytes(input.AsSpan(12), (uint)kind);
        if (DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_EXTERNAL_BACKING, input, input.Length, 0, 0, out _, 0)) return 0;
        int code = Marshal.GetLastPInvokeError();
        // Data that does not get smaller is left as it is; that is not a failure.
        return code == ERROR_COMPRESSION_NOT_BENEFICIAL ? 0 : code;
    }

    /// <summary>All files under a folder (or the file itself), never following links into other places.</summary>
    static IEnumerable<string> Files(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            yield return path;
            yield break;
        }
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo) stack.Push(e.FullName);
                else yield return e.FullName;
            }
        }
    }
}
