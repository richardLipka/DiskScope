using System.Buffers.Binary;

namespace DiskScope.Core.Scanning.Mft;

/// <summary>A file name ($FILE_NAME attribute): one link of a file into a directory.</summary>
internal readonly ref struct MftName(uint parentRecord, ReadOnlySpan<char> name)
{
    public readonly uint ParentRecord = parentRecord;
    public readonly ReadOnlySpan<char> Name = name;
}

/// <summary>What one MFT record (FILE record segment) says about its file.</summary>
internal ref struct MftRecord
{
    public uint Number;
    /// <summary>For an extension record, the base record it belongs to; otherwise the record itself.</summary>
    public uint BaseRecord;
    public bool IsExtension;
    public bool IsDirectory;
    public uint Attributes;
    public long MTime;
    /// <summary>Clusters allocated to all data streams, in bytes.</summary>
    public long OnDisk;
    /// <summary>Logical size of the unnamed data stream, or -1 when this record does not carry it.</summary>
    public long Length;
    public uint ReparseTag;
}

/// <summary>Decodes NTFS FILE records. Pure byte parsing, no I/O, so it can be tested on synthetic records.</summary>
internal static class MftParser
{
    public const uint RootRecord = 5;
    const int SectorStride = 512;
    const ulong RecordMask = 0xFFFF_FFFF_FFFF;

    const uint AttrStandardInformation = 0x10;
    const uint AttrFileName = 0x30;
    const uint AttrData = 0x80;
    const uint AttrReparsePoint = 0xC0;
    const uint AttrEnd = 0xFFFF_FFFF;

    const ushort RecordInUse = 0x1;
    const ushort RecordIsDirectory = 0x2;
    const ushort AttrFlagCompressed = 0x1;
    const ushort AttrFlagSparse = 0x8000;
    const byte NameTypeDos = 2;

    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b[o..]);

    /// <summary>Undoes the update sequence array (torn-write protection) in place. False if the record is damaged.</summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        int usaOffset = U16(rec, 4);
        int usaCount = U16(rec, 6);
        if (usaCount == 0 || usaOffset + usaCount * 2 > rec.Length) return false;
        ushort usn = U16(rec, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * SectorStride - 2;
            if (pos + 2 > rec.Length) break;
            if (U16(rec, pos) != usn) return false;
            rec[pos] = rec[usaOffset + 2 * i];
            rec[pos + 1] = rec[usaOffset + 2 * i + 1];
        }
        return true;
    }

    public delegate void NameVisitor(in MftName name);

    /// <summary>
    /// Parses a record whose fixups have been applied. Returns false for free or invalid records.
    /// Each non-DOS file name is passed to <paramref name="onName"/> while the record buffer is still valid.
    /// </summary>
    public static bool Parse(ReadOnlySpan<byte> rec, uint number, out MftRecord result, NameVisitor onName)
    {
        result = default;
        if (rec.Length < 48 || U32(rec, 0) != 0x454C4946) return false; // "FILE"
        ushort flags = U16(rec, 22);
        if ((flags & RecordInUse) == 0) return false;

        ulong baseRef = (ulong)I64(rec, 32) & RecordMask;
        result.Number = number;
        result.IsExtension = baseRef != 0;
        result.BaseRecord = result.IsExtension ? (uint)baseRef : number;
        result.IsDirectory = (flags & RecordIsDirectory) != 0;
        result.Length = -1;

        int end = Math.Min((int)U32(rec, 24), rec.Length);
        int off = U16(rec, 20);
        while (off + 16 <= end)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, off + 4);
            if (len < 16 || off + len > end) break;
            bool nonResident = rec[off + 8] != 0;
            int nameLength = rec[off + 9];
            var attr = rec.Slice(off, len);
            ReadOnlySpan<byte> value = nonResident ? default : ResidentValue(attr);

            switch (type)
            {
                case AttrStandardInformation when value.Length >= 36:
                    result.MTime = I64(value, 8);
                    result.Attributes = U32(value, 32);
                    break;
                case AttrFileName when value.Length >= 66:
                    int chars = value[64];
                    if (value[65] != NameTypeDos && 66 + chars * 2 <= value.Length)
                    {
                        uint parent = (uint)((ulong)I64(value, 0) & RecordMask);
                        var name = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(value.Slice(66, chars * 2));
                        onName(new MftName(parent, name));
                    }
                    break;
                case AttrData when nonResident && len >= 64:
                    if (I64(attr, 16) != 0) break; // later extent of a stream split across records
                    ushort attrFlags = U16(attr, 12);
                    long allocated = I64(attr, 40);
                    if ((attrFlags & (AttrFlagCompressed | AttrFlagSparse)) != 0 && len >= 72) allocated = I64(attr, 64);
                    else allocated = WithoutSparseRuns(attr, allocated);
                    result.OnDisk += allocated;
                    if (nameLength == 0) result.Length = I64(attr, 48);
                    break;
                case AttrData when !nonResident && nameLength == 0:
                    result.Length = value.Length;
                    break;
                case AttrReparsePoint when value.Length >= 4:
                    result.ReparseTag = U32(value, 0);
                    break;
            }
            off += len;
        }
        if (result.IsDirectory) result.Attributes |= Native.NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
        return true;
    }

    static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < 24) return default;
        int length = (int)U32(attr, 16);
        int offset = U16(attr, 20);
        return offset + length <= attr.Length ? attr.Slice(offset, length) : default;
    }

    /// <summary>
    /// Subtracts sparse runs (holes with no clusters) from the allocated size. Needed for streams that are sparse
    /// without the sparse flag, such as $BadClus:$Bad, which spans the whole volume but occupies nothing.
    /// </summary>
    static long WithoutSparseRuns(ReadOnlySpan<byte> attr, long allocated)
    {
        long clusters = I64(attr, 24) - I64(attr, 16) + 1; // HighestVcn - LowestVcn + 1
        // Only when this extent holds the whole stream, so that allocated / clusters is the cluster size.
        if (clusters <= 0 || allocated % clusters != 0) return allocated;
        long clusterSize = allocated / clusters;
        if (clusterSize < 512 || clusterSize > (2 << 20) || (clusterSize & (clusterSize - 1)) != 0) return allocated;
        long sparse = 0;
        int pos = U16(attr, 32);
        while (pos < attr.Length && attr[pos] != 0)
        {
            int lenBytes = attr[pos] & 0xF, offBytes = attr[pos] >> 4;
            if (pos + 1 + lenBytes + offBytes > attr.Length) return allocated;
            long count = 0;
            for (int i = 0; i < lenBytes; i++) count |= (long)attr[pos + 1 + i] << (8 * i);
            if (offBytes == 0) sparse += count;
            pos += 1 + lenBytes + offBytes;
        }
        return sparse == 0 ? allocated : Math.Max(0, allocated - sparse * clusterSize);
    }

    /// <summary>Decodes a non-resident attribute's run list into (starting cluster, cluster count) pairs; sparse runs have LCN -1.</summary>
    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> attr)
    {
        var runs = new List<(long, long)>();
        int pos = U16(attr, 32);
        long lcn = 0;
        while (pos < attr.Length && attr[pos] != 0)
        {
            int lenBytes = attr[pos] & 0xF, offBytes = attr[pos] >> 4;
            pos++;
            long count = 0;
            for (int i = 0; i < lenBytes; i++) count |= (long)attr[pos + i] << (8 * i);
            pos += lenBytes;
            if (offBytes == 0)
            {
                runs.Add((-1, count));
                continue;
            }
            long delta = 0;
            for (int i = 0; i < offBytes; i++) delta |= (long)attr[pos + i] << (8 * i);
            if ((attr[pos + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes); // sign-extend
            pos += offBytes;
            lcn += delta;
            runs.Add((lcn, count));
        }
        return runs;
    }

    /// <summary>Finds the unnamed $DATA attribute of a record (for $MFT itself) and returns its runs and data size.</summary>
    public static (List<(long Lcn, long Clusters)> Runs, long DataSize) FindDataRuns(ReadOnlySpan<byte> rec)
    {
        int end = Math.Min((int)U32(rec, 24), rec.Length);
        int off = U16(rec, 20);
        while (off + 16 <= end)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, off + 4);
            if (len < 16 || off + len > end) break;
            if (type == AttrData && rec[off + 8] != 0 && rec[off + 9] == 0)
                return (DecodeRuns(rec.Slice(off, len)), I64(rec, off + 48));
            off += len;
        }
        throw new InvalidDataException("$MFT has no data attribute.");
    }
}
