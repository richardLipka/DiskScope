using System.Text.Json;

namespace DiskScope.Core.Storage;

/// <summary>
/// How full each drive was over time: one small JSON file with a few readings per drive. It lets the drive overview
/// tell how much more is used now than at the last scan, without scanning again.
/// </summary>
public sealed class DriveUsageLog(string filePath)
{
    public sealed record Entry(DateTime TimeUtc, long Used, long Total);

    /// <summary>Readings closer together than this replace each other (unless forced, as at the end of a scan).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    /// <summary>Readings kept per drive; the oldest are dropped.</summary>
    public const int MaxEntries = 400;

    Dictionary<string, List<Entry>>? _drives;

    public string FilePath { get; } = filePath;

    static string Key(string root) => root.TrimEnd('\\').ToUpperInvariant() + "\\";

    Dictionary<string, List<Entry>> Drives => _drives ??= Load();

    Dictionary<string, List<Entry>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(File.ReadAllText(FilePath));
                if (data != null)
                    return data.Where(d => d.Value != null).ToDictionary(d => Key(d.Key),
                        d => d.Value.Where(e => e != null && e.Used >= 0 && e.Total >= 0).OrderBy(e => e.TimeUtc).ToList());
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged or unreadable log only loses the history; it must not stop the app.
        }
        return [];
    }

    /// <summary>Adds a reading. A reading within <see cref="MinInterval"/> of the last one replaces it, unless forced.</summary>
    public void Record(string root, long used, long total, DateTime nowUtc, bool force = false)
    {
        var list = Drives.TryGetValue(Key(root), out var l) ? l : Drives[Key(root)] = [];
        if (list.Count > 0 && nowUtc - list[^1].TimeUtc < MinInterval && !force)
            list[^1] = new Entry(nowUtc, used, total);
        else list.Add(new Entry(nowUtc, used, total));
        if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
    }

    /// <summary>The reading closest to a moment (such as a scan's time), if one lies within a day of it.</summary>
    public Entry? At(string root, DateTime timeUtc)
    {
        if (!Drives.TryGetValue(Key(root), out var list) || list.Count == 0) return null;
        var best = list.MinBy(e => Math.Abs((e.TimeUtc - timeUtc).Ticks))!;
        return Math.Abs((best.TimeUtc - timeUtc).TotalHours) <= 24 ? best : null;
    }

    public IReadOnlyList<Entry> History(string root) => Drives.TryGetValue(Key(root), out var list) ? list : [];

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Drives));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to keep the history is not worth an error message.
        }
    }
}
