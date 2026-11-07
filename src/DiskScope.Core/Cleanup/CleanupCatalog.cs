using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskScope.Core.Cleanup;

public enum CleanupSafety
{
    /// <summary>Caches and temporary data: deleting them loses nothing that cannot be recreated.</summary>
    Safe,
    /// <summary>Deletable, but with a cost (re-download, lost offline copies, lost rollback) or better done by the owning app.</summary>
    Caution,
    /// <summary>Must not be deleted by hand; the description says which Windows tool frees the space properly.</summary>
    System,
}

public enum CleanupAction
{
    /// <summary>Only advice is shown; the app offers no delete.</summary>
    None,
    /// <summary>Delete what is inside the folder, keeping the folder itself (Temp, caches).</summary>
    DeleteContents,
    /// <summary>Delete the matched file or folder itself.</summary>
    Delete,
}

/// <summary>One known location: where it is, how safe it is to clean, and what to tell the user — in several languages.</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public CleanupSafety Safety { get; set; } = CleanupSafety.Caution;
    public CleanupAction Action { get; set; } = CleanupAction.None;

    /// <summary>
    /// Full path patterns. Environment variables (%SystemRoot%), "{user}" for every user profile folder,
    /// and * ? wildcards in any segment are allowed.
    /// </summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>
    /// Folder name patterns matched anywhere in the tree (e.g. node_modules); nested matches are skipped.
    /// A pattern with backslashes is matched as the end of a path (e.g. .git\lfs\objects in any repository).
    /// </summary>
    public List<string> FolderNames { get; set; } = [];

    /// <summary>A cache of downloaded files: its tool downloads what it needs again, so emptying it loses no work.</summary>
    public bool Redownloadable { get; set; }

    /// <summary>Matches smaller than this many bytes are not reported.</summary>
    public long MinSize { get; set; }

    /// <summary>In a user's override file: removes the built-in entry with the same id.</summary>
    public bool Disabled { get; set; }

    public Dictionary<string, string> Name { get; set; } = [];
    public Dictionary<string, string> Description { get; set; } = [];
    public Dictionary<string, string> HowTo { get; set; } = [];

    /// <summary>Text in the given language, falling back to English, then to whatever is there.</summary>
    public static string Text(Dictionary<string, string> texts, string lang) =>
        texts.TryGetValue(lang, out var s) || texts.TryGetValue("en", out s) ? s : texts.Values.FirstOrDefault() ?? "";
}

/// <summary>
/// The catalog of known locations. A default ships inside the app; a user's JSON file of the same format
/// can replace entries (same id), disable them ("disabled": true) or add new ones, without rebuilding the app.
/// </summary>
public sealed class CleanupCatalog
{
    public const string FileName = "cleanup-catalog.json";

    public int Version { get; set; } = 1;
    public List<CatalogEntry> Entries { get; set; } = [];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiskScope.Core.Cleanup." + FileName)
                           ?? throw new InvalidOperationException("Built-in catalog is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static CleanupCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<CleanupCatalog>(json, Options) ?? new CleanupCatalog();
        foreach (var e in catalog.Entries)
            if (string.IsNullOrWhiteSpace(e.Id)) throw new JsonException("Every catalog entry needs an \"id\".");
        return catalog;
    }

    public static CleanupCatalog Default() => Parse(DefaultJson());

    /// <summary>
    /// The built-in catalog merged with the user's file when it exists. A broken user file is reported in
    /// <paramref name="error"/> and ignored, so the built-in catalog still works.
    /// </summary>
    public static CleanupCatalog Load(string? userFile, out string? error)
    {
        error = null;
        var catalog = Default();
        if (userFile == null || !File.Exists(userFile)) return catalog;
        try
        {
            catalog.Merge(Parse(File.ReadAllText(userFile)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        return catalog;
    }

    /// <summary>Applies another catalog on top: same id replaces (or removes, when disabled), new ids are added.</summary>
    public void Merge(CleanupCatalog overrides)
    {
        foreach (var entry in overrides.Entries)
        {
            int i = Entries.FindIndex(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
            if (entry.Disabled)
            {
                if (i >= 0) Entries.RemoveAt(i);
            }
            else if (i >= 0) Entries[i] = entry;
            else Entries.Add(entry);
        }
    }
}
