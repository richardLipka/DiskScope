using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskScope.Core.Duplicates;

/// <summary>
/// Replaces duplicate files with hard links to one kept file, so every name stays and opens the same content while
/// the data is stored once. Afterwards the names share one file: changing it through any name changes it for all,
/// which is why this suits collections that are read, not edited (photos, installers, downloaded libraries).
/// </summary>
public static class HardLinker
{
    /// <summary>Error texts the UI translates.</summary>
    public const string ErrorOtherDrive = "#other-drive";
    public const string ErrorChanged = "#changed";
    public const string ErrorDiffers = "#differs";

    const int SampleSize = 64 * 1024;
    const uint MOVEFILE_REPLACE_EXISTING = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, nint security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileExW(string existing, string replacement, uint flags);

    /// <summary>
    /// Replaces <paramref name="copy"/> with a hard link to <paramref name="keeper"/>. The copy is checked first: same
    /// drive, still the size and write time it had when scanned, and the same first and last 64 KB as the keeper.
    /// The link is created under a temporary name and then renamed over the copy, so the copy is never missing.
    /// </summary>
    /// <returns>Null on success, otherwise why not.</returns>
    public static string? Replace(string keeper, string copy, long expectedLength, long? expectedMTime)
    {
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(keeper)), Path.GetPathRoot(Path.GetFullPath(copy)), StringComparison.OrdinalIgnoreCase))
            return ErrorOtherDrive;
        string k = LongPath.Of(keeper), c = LongPath.Of(copy);
        var copyInfo = new FileInfo(c);
        var keeperInfo = new FileInfo(k);
        if (!copyInfo.Exists || !keeperInfo.Exists) return new FileNotFoundException(null, copyInfo.Exists ? keeper : copy).Message;
        if (copyInfo.Length != expectedLength || keeperInfo.Length != expectedLength ||
            expectedMTime is long mtime && mtime != 0 && copyInfo.LastWriteTimeUtc.ToFileTimeUtc() != mtime)
            return ErrorChanged;
        if (!SameSamples(k, c, expectedLength)) return ErrorDiffers;

        string temp = c + ".dslink-" + Guid.NewGuid().ToString("N")[..8];
        if (!CreateHardLinkW(temp, k, 0)) return new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        try
        {
            if ((copyInfo.Attributes & FileAttributes.ReadOnly) != 0) copyInfo.Attributes &= ~FileAttributes.ReadOnly;
            if (!MoveFileExW(temp, c, MOVEFILE_REPLACE_EXISTING)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return ex.Message;
        }
    }

    /// <summary>The duplicate search hashed the whole files; this re-reads both ends in case one changed since.</summary>
    static bool SameSamples(string a, string b, long length)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ba = new byte[SampleSize];
        var bb = new byte[SampleSize];
        foreach (long offset in length > SampleSize ? new[] { 0L, length - SampleSize } : [0L])
        {
            fa.Position = fb.Position = offset;
            int na = fa.ReadAtLeast(ba, (int)Math.Min(SampleSize, length - offset), false);
            int nb = fb.ReadAtLeast(bb, (int)Math.Min(SampleSize, length - offset), false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
        return true;
    }

    /// <summary>How many names a file has (1 for an ordinary file).</summary>
    public static int LinkCount(string path)
    {
        using var fs = new FileStream(LongPath.Of(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(fs.SafeFileHandle, out var info) ? (int)info.NumberOfLinks : 1;
    }

    // FILETIME fields are only 4-byte aligned, hence Pack = 4.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
