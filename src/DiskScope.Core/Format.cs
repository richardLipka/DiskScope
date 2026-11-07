using System.Globalization;

namespace DiskScope.Core;

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count the way Explorer does (1 KB = 1024 B).</summary>
    public static string Size(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double v = Math.Abs(bytes);
        int unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        string sign = bytes < 0 ? "-" : "";
        string num = unit == 0 ? v.ToString("0", culture) : v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", culture);
        return $"{sign}{num} {Units[unit]}";
    }

    public static string SignedSize(long bytes, CultureInfo? culture = null) =>
        bytes > 0 ? "+" + Size(bytes, culture) : Size(bytes, culture);

    public static string Count(long n, CultureInfo? culture = null) => n.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    /// <summary>The largest FILETIME a DateTime can hold (31 Dec 9999); Windows allows larger ones.</summary>
    static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// A FILETIME that DateTime can represent, else 0 (unknown). Damaged MFT records, crafted snapshots or odd files
    /// can carry times DateTime.FromFileTimeUtc would throw on.
    /// </summary>
    public static long ValidFileTime(long filetime) => filetime > 0 && filetime <= MaxFileTime ? filetime : 0;

    /// <summary>Converts a FILETIME without ever throwing; null when it is unknown or out of range.</summary>
    public static DateTime? FileTime(long filetime) =>
        ValidFileTime(filetime) is long v and > 0 ? DateTime.FromFileTimeUtc(v) : null;

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
