using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskScope.Core.Native;
using DiskScope.Core.Scanning.Mft;
using Microsoft.Win32.SafeHandles;
using static DiskScope.Core.Native.NativeMethods;

namespace DiskScope.Core.Scanning;

/// <summary>
/// Reads the NTFS Master File Table straight from the volume: one sequential read instead of
/// opening every directory. Requires elevation. A reader thread fills buffers while this thread parses.
/// </summary>
public sealed unsafe class MftScanner : IScanner
{
    const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    const int ChunkBytes = 4 << 20;

    public ScanMode Mode => ScanMode.Mft;

    /// <summary>True for the root of an NTFS volume when the process is elevated.</summary>
    public static bool CanScan(string root)
    {
        if (root.Length != 3 || root[1] != ':' || !Elevation.IsElevated) return false;
        try
        {
            return new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Scan(NodeStore store, string rootPath, ScanProgress progress, CancellationToken cancel)
    {
        using var volume = CreateFile(@"\\.\" + rootPath[..2], GENERIC_READ, FILE_SHARE_ALL, 0, OPEN_EXISTING, 0, 0);
        if (volume.IsInvalid) throw new IOException($"Cannot open volume {rootPath[..2]}.", Marshal.GetHRForLastWin32Error());

        var data = stackalloc byte[128];
        if (!DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, 128, out _, 0))
            throw new IOException("Not an NTFS volume.", Marshal.GetHRForLastWin32Error());
        int bytesPerCluster = *(int*)(data + 44);
        int recordSize = *(int*)(data + 48);
        long mftStartLcn = *(long*)(data + 64);

        // Record 0 is $MFT itself; its data runs say where the rest of the table lies.
        var first = new byte[Math.Max(recordSize, 4096)];
        ReadAt(volume, mftStartLcn * bytesPerCluster, first, first.Length);
        var record0 = first.AsSpan(0, recordSize);
        if (!MftParser.ApplyFixup(record0)) throw new InvalidDataException("$MFT record is damaged.");
        var (runs, dataSize) = MftParser.FindDataRuns(record0);

        long totalRecords = dataSize / recordSize;
        progress.TotalRecords = totalRecords;
        var builder = new MftBuilder(store, progress, totalRecords);

        using var full = new BlockingCollection<(nint Buffer, int Bytes, long FirstRecord)>(boundedCapacity: 3);
        var free = new ConcurrentBag<nint>();
        for (int i = 0; i < 4; i++) free.Add((nint)NativeMemory.AlignedAlloc(ChunkBytes, 4096));
        var readerCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Exception? readError = null;

        var reader = new Thread(() =>
        {
            try
            {
                long record = 0;
                foreach (var (lcn, clusters) in runs)
                {
                    if (lcn < 0) { record += clusters * bytesPerCluster / recordSize; continue; }
                    long offset = lcn * bytesPerCluster, remaining = clusters * bytesPerCluster;
                    while (remaining > 0 && record < totalRecords)
                    {
                        nint buf;
                        while (!free.TryTake(out buf)) Thread.Sleep(1);
                        int bytes = (int)Math.Min(remaining, ChunkBytes);
                        ReadAt(volume, offset, (byte*)buf, bytes);
                        full.Add((buf, bytes, record), readerCancel.Token);
                        record += bytes / recordSize;
                        offset += bytes;
                        remaining -= bytes;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                readError = ex;
            }
            finally
            {
                full.CompleteAdding();
            }
        }) { IsBackground = true, Name = "MftReader" };
        reader.Start();

        try
        {
            foreach (var (buf, bytes, firstRecord) in full.GetConsumingEnumerable(cancel))
            {
                var chunk = new Span<byte>((byte*)buf, bytes);
                for (int i = 0; i + recordSize <= bytes; i += recordSize)
                {
                    long number = firstRecord + i / recordSize;
                    if (number >= totalRecords) break;
                    var rec = chunk.Slice(i, recordSize);
                    if (MftParser.ApplyFixup(rec)) builder.AddRecord(rec, (uint)number);
                }
                progress.Records = Math.Min(totalRecords, firstRecord + bytes / recordSize);
                free.Add(buf);
            }
        }
        finally
        {
            readerCancel.Cancel();
            reader.Join();
            while (free.TryTake(out var b)) NativeMemory.AlignedFree((void*)b);
            while (full.TryTake(out var item)) NativeMemory.AlignedFree((void*)item.Buffer);
        }
        if (readError != null) throw new IOException("Reading the MFT failed: " + readError.Message, readError);
        cancel.ThrowIfCancellationRequested();

        progress.CurrentPath = null;
        builder.Finish();
        progress.Records = totalRecords;
    }

    static void ReadAt(SafeFileHandle volume, long offset, byte[] buffer, int bytes)
    {
        fixed (byte* p = buffer) ReadAt(volume, offset, p, bytes);
    }

    static void ReadAt(SafeFileHandle volume, long offset, byte* buffer, int bytes)
    {
        if (!SetFilePointerEx(volume, offset, out _, 0) || !ReadFile(volume, buffer, (uint)bytes, out uint read, 0) || read != bytes)
            throw new IOException($"Volume read failed at {offset}.", Marshal.GetHRForLastWin32Error());
    }
}
