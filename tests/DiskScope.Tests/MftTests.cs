using System.Buffers.Binary;
using System.Text;
using DiskScope.Core;
using DiskScope.Core.Native;
using DiskScope.Core.Scanning;
using DiskScope.Core.Scanning.Mft;
using Xunit.Abstractions;

namespace DiskScope.Tests;

public class MftTests(ITestOutputHelper output)
{
    const int RecordSize = 1024;

    // --- synthetic NTFS records ---

    static byte[] Attr(uint type, byte[] body, bool nonResident = false, string name = "")
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        const int nameOffset = 24;
        if (nonResident)
        {
            // body is the non-resident header from offset 16 on, mapping pairs included; the caller places the name
            var a = new byte[Align(16 + body.Length + nameBytes.Length)];
            W32(a, 0, type);
            W32(a, 4, (uint)a.Length);
            a[8] = 1;
            a[9] = (byte)name.Length;
            body.CopyTo(a, 16);
            return a;
        }
        int valueOffset = Align(nameOffset + nameBytes.Length);
        var r = new byte[Align(valueOffset + body.Length)];
        W32(r, 0, type);
        W32(r, 4, (uint)r.Length);
        r[9] = (byte)name.Length;
        W16(r, 10, (ushort)nameOffset);
        W32(r, 16, (uint)body.Length);
        W16(r, 20, (ushort)valueOffset);
        nameBytes.CopyTo(r, nameOffset);
        body.CopyTo(r, valueOffset);
        return r;
    }

    static int Align(int n) => (n + 7) & ~7;
    static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    static void W64(byte[] b, int o, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(o), v);

    static byte[] StdInfo(long mtime, uint attrs = 0x20)
    {
        var v = new byte[72];
        W64(v, 8, mtime);
        W32(v, 32, attrs);
        return Attr(0x10, v);
    }

    static byte[] FileName(uint parent, string name, byte type = 1)
    {
        var v = new byte[66 + name.Length * 2];
        W64(v, 0, (long)parent | (1L << 48)); // sequence number in the top bits must be masked off
        v[64] = (byte)name.Length;
        v[65] = type;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
        return Attr(0x30, v);
    }

    static byte[] NonResidentData(long allocated, long size, long? compressed = null, string name = "", long lowestVcn = 0)
    {
        int header = compressed.HasValue ? 72 : 64;
        var b = new byte[header - 16 + 8];
        W64(b, 0, lowestVcn);
        W16(b, 16, (ushort)header);
        W64(b, 24, allocated);
        W64(b, 32, size);
        W64(b, 40, size);
        if (compressed.HasValue) W64(b, 48, compressed.Value);
        var a = Attr(0x80, b, nonResident: true, name: name);
        if (compressed.HasValue) W16(a, 12, 0x8000);
        if (name.Length > 0)
        {
            W16(a, 10, (ushort)(header + 8));
            Encoding.Unicode.GetBytes(name).CopyTo(a, header + 8);
        }
        return a;
    }

    static byte[] ResidentData(int length) => Attr(0x80, new byte[length]);

    static byte[] Record(uint number, bool dir, params byte[][] attrs) => Record(number, dir, 0, true, attrs);

    static byte[] Record(uint number, bool dir, uint baseRecord, bool inUse, params byte[][] attrs)
    {
        var r = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(r, 0);
        W16(r, 4, 48);
        W16(r, 6, 3);
        W16(r, 20, 56);
        W16(r, 22, (ushort)((inUse ? 1 : 0) | (dir ? 2 : 0)));
        W64(r, 32, baseRecord == 0 ? 0 : baseRecord | (1L << 48));
        W32(r, 44, number);
        int off = 56;
        foreach (var a in attrs)
        {
            a.CopyTo(r, off);
            off += a.Length;
        }
        W32(r, off, 0xFFFFFFFF);
        W32(r, 24, (uint)(off + 8));
        W32(r, 28, RecordSize);
        // Update sequence: the last two bytes of each 512-byte sector move into the array.
        const ushort usn = 0x0101;
        W16(r, 48, usn);
        for (int i = 1; i <= 2; i++)
        {
            int pos = i * 512 - 2;
            r[48 + 2 * i] = r[pos];
            r[48 + 2 * i + 1] = r[pos + 1];
            W16(r, pos, usn);
        }
        return r;
    }

    [Fact]
    public void DecodesRunLists()
    {
        var attr = new byte[80];
        W16(attr, 32, 64);
        // 0x21: 1 length byte, 2 offset bytes. Run 1: 16 clusters at 0x1000; run 2: 8 clusters at -0x100 relative; sparse 4.
        new byte[] { 0x21, 0x10, 0x00, 0x10, 0x21, 0x08, 0x00, 0xFF, 0x01, 0x04, 0x00 }.CopyTo(attr, 64);
        var runs = MftParser.DecodeRuns(attr);
        Assert.Equal([(0x1000L, 16L), (0x0F00L, 8L), (-1L, 4L)], runs);
    }

    [Fact]
    public void FixupDetectsTornRecords()
    {
        var rec = Record(16, false, StdInfo(1), FileName(5, "x"));
        Assert.True(MftParser.ApplyFixup(rec.AsSpan()));
        var torn = Record(16, false, StdInfo(1), FileName(5, "x"));
        torn[1022] ^= 0xFF;
        Assert.False(MftParser.ApplyFixup(torn.AsSpan()));
    }

    [Fact]
    public void BuildsTreeFromRecords()
    {
        var records = new Dictionary<uint, byte[]>
        {
            [5] = Record(5, true, StdInfo(777), FileName(5, ".")),
            [16] = Record(16, true, StdInfo(1), FileName(5, "Users")),
            [17] = Record(17, false, StdInfo(2), FileName(16, "a.bin"), NonResidentData(8192, 5000)),
            [18] = Record(18, false, StdInfo(3), FileName(5, "small.txt"), ResidentData(100)),
            [19] = Record(19, false, StdInfo(4), FileName(16, "h1", 0), FileName(5, "h2", 0), NonResidentData(4096, 4000)),
            [20] = Record(20, false, StdInfo(5), FileName(5, "LONGFI~1.TXT", 2), FileName(5, "Long file name.txt", 1), ResidentData(10)),
            [21] = Record(21, false, 0, false, StdInfo(6), FileName(5, "free.bin")),
            [22] = Record(22, false, 23, true, NonResidentData(1_000_000, 999_000)),
            [23] = Record(23, false, StdInfo(7), FileName(16, "big.vhd")),
            [24] = Record(24, false, StdInfo(8), FileName(999, "orphan.dat")),
            [25] = Record(25, false, StdInfo(9), FileName(5, "sparse.img"), NonResidentData(1L << 30, 1L << 30, compressed: 65536)),
            [26] = Record(26, false, StdInfo(10), FileName(27, "child.txt"), NonResidentData(4096, 10), NonResidentData(8192, 8000, name: "Zone")),
            [27] = Record(27, true, StdInfo(11), FileName(5, "Later")),
            [28] = Record(28, true, StdInfo(12), FileName(5, "Junction"), Attr(0xC0, BitConverter.GetBytes(NativeMethods.IO_REPARSE_TAG_MOUNT_POINT))),
        };

        var store = new NodeStore();
        store.CreateRoot(@"T:\");
        var progress = new ScanProgress();
        var builder = new MftBuilder(store, progress, 30);
        foreach (var (number, rec) in records.OrderBy(r => r.Key))
            if (MftParser.ApplyFixup(rec.AsSpan())) builder.AddRecord(rec, number);
        builder.Finish();

        ref var root = ref store[store.Root];
        Assert.Equal(777, root.MTime);
        int users = store.FindPath(@"T:\Users");
        Assert.True(store[users].IsDirectory);
        Assert.Equal(8192, store[store.FindPath(@"T:\Users\a.bin")].Size);
        Assert.Equal(5000, store[store.FindPath(@"T:\Users\a.bin")].Length);

        int small = store.FindPath(@"T:\small.txt");
        Assert.Equal(0, store[small].Size);
        Assert.Equal(100, store[small].Length);

        int h1 = store.FindPath(@"T:\Users\h1"), h2 = store.FindPath(@"T:\h2");
        Assert.Equal(4096, store[h1].Size);
        Assert.Equal(0, store[h2].Size);
        Assert.True((store[h2].Flags & NodeFlags.HardLink) != 0);

        Assert.NotEqual(NodeStore.None, store.FindPath(@"T:\Long file name.txt"));
        Assert.Equal(NodeStore.None, store.FindPath(@"T:\LONGFI~1.TXT"));
        Assert.Equal(NodeStore.None, store.FindPath(@"T:\free.bin"));

        int big = store.FindPath(@"T:\Users\big.vhd");
        Assert.Equal(1_000_000, store[big].Size);
        Assert.Equal(999_000, store[big].Length);

        Assert.NotEqual(NodeStore.None, store.FindPath(@"T:\orphan.dat"));
        Assert.Equal(65536, store[store.FindPath(@"T:\sparse.img")].Size);

        int child = store.FindPath(@"T:\Later\child.txt");
        Assert.Equal(4096 + 8192, store[child].Size); // alternate data streams take disk space too
        Assert.Equal(10, store[child].Length);        // but the logical size is the main stream's

        Assert.True((store[store.FindPath(@"T:\Junction")].Flags & NodeFlags.Reparse) != 0);

        Assert.Equal(9, root.FileCount);
        Assert.Equal(3, root.DirCount);
        Assert.Equal(8192 + 4096 + 1_000_000 + 65536 + 4096 + 8192, root.Size);
        Assert.Equal(8192 + 4096 + 1_000_000, store[users].Size);
    }

    [Fact]
    public void ScansRealVolumeWhenElevated()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        if (!MftScanner.CanScan(root))
        {
            output.WriteLine("Skipped: needs elevation and NTFS.");
            return;
        }
        var mft = new ScanSession(root, ScanMode.Mft);
        mft.Start();
        mft.Wait();
        Assert.Equal(ScanState.Completed, mft.State);
        Assert.Null(mft.MftError);
        var s = mft.Store;
        output.WriteLine($"MFT: {s[s.Root].FileCount:N0} files, {s[s.Root].DirCount:N0} dirs, {Format.Size(s[s.Root].Size)} in {mft.Elapsed.TotalSeconds:0.0}s");
        var drive = new DriveInfo(root);
        long used = drive.TotalSize - drive.TotalFreeSpace;
        output.WriteLine($"Volume used: {Format.Size(used)}");
        // Allocated file data is a bit less than used space (metadata, indexes, shadow copies).
        Assert.InRange(s[s.Root].Size, used * 6 / 10, used * 11 / 10);

        // Compare one folder against the directory scanner.
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dir = new ScanSession(folder, ScanMode.Directory);
        dir.Start();
        dir.Wait();
        int inMft = s.FindPath(folder);
        output.WriteLine($"{folder}: MFT {s[inMft].FileCount:N0} files {Format.Size(s[inMft].Size)}, " +
                         $"dirs {dir.Store[dir.Store.Root].FileCount:N0} files {Format.Size(dir.Store[dir.Store.Root].Size)}");
        Assert.InRange(s[inMft].Size, dir.Store[dir.Store.Root].Size * 9 / 10, dir.Store[dir.Store.Root].Size * 11 / 10);
    }
}
