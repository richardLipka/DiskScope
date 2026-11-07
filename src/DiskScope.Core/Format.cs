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

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
