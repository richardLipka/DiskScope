using System.Text.Json;

namespace DiskScope;

/// <summary>Portable settings stored next to the executable (or in LocalAppData when that is read-only).</summary>
sealed class AppSettings
{
    public string Language { get; set; } = L.DefaultLanguage;
    public string? LastPath { get; set; }
    public int[]? ColumnWidths { get; set; }
    public Rectangle? WindowBounds { get; set; }
    public bool Maximized { get; set; }

    static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class AppPaths
{
    static string? _dataDir;

    /// <summary>The "data" folder beside the executable, or LocalAppData\DiskScope if that is not writable.</summary>
    public static string DataDir => _dataDir ??= ResolveDataDir();

    static string ResolveDataDir()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            string probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskScope");
        }
    }
}
