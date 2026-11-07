using DiskScope.Core;
using DiskScope.Core.Cleanup;

namespace DiskScope.Tests;

public class CleanupTests
{
    [Fact]
    public void BuiltInCatalogIsCompleteInBothLanguages()
    {
        var catalog = CleanupCatalog.Default();
        Assert.True(catalog.Entries.Count >= 30);
        Assert.Equal(catalog.Entries.Count, catalog.Entries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in catalog.Entries)
        {
            foreach (var texts in new[] { e.Name, e.Description, e.HowTo })
            {
                Assert.False(string.IsNullOrWhiteSpace(texts.GetValueOrDefault("cs")), $"{e.Id}: missing Czech text");
                Assert.False(string.IsNullOrWhiteSpace(texts.GetValueOrDefault("en")), $"{e.Id}: missing English text");
            }
            Assert.True(e.Paths.Count + e.FolderNames.Count > 0, $"{e.Id}: no paths or folder names");
            Assert.False(e.Safety == CleanupSafety.System && e.Action != CleanupAction.None, $"{e.Id}: system item offers deleting");
        }
    }

    /// <summary>C:\ with two user profiles, Temp, a Chrome cache, Windows folders and node_modules (one nested).</summary>
    static NodeStore SampleDrive()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int Dir(int parent, string name) => Link(s, parent, s.Add(parent, name, NodeFlags.Directory, 0, 0, 0, 0));
        void File(int parent, string name, long size) => Link(s, parent, s.Add(parent, name, NodeFlags.None, 0, size, size, 0));

        int users = Dir(root, "Users");
        foreach (var user in new[] { "alice", "bob" })
        {
            int home = Dir(users, user);
            int local = Dir(Dir(home, "AppData"), "Local");
            File(Dir(local, "Temp"), "x.tmp", 1000);
            int profile = Dir(Dir(Dir(Dir(local, "Google"), "Chrome"), "User Data"), "Default");
            File(Dir(profile, "Cache"), "data_1", 5000);
        }
        int project = Dir(Dir(s.FindPath(@"C:\Users\alice"), "src"), "web");
        int nm = Dir(project, "node_modules");
        File(nm, "big.js", 90_000_000);
        File(Dir(Dir(nm, "pkg"), "node_modules"), "inner.js", 1000);
        int windows = Dir(root, "Windows");
        File(Dir(windows, "Temp"), "w.log", 2000);
        File(Dir(windows, "WinSxS"), "c.dll", 3000);
        s.ComputeTotalsFromRoot();
        return s;
    }

    static int Link(NodeStore s, int parent, int child)
    {
        s[child].NextSibling = s[parent].FirstChild;
        s[parent].FirstChild = child;
        return child;
    }

    static string Expand(string p) => p.Replace("%SystemDrive%", "C:").Replace("%SystemRoot%", @"C:\Windows");

    [Fact]
    public void ResolvesUserPlaceholdersWildcardsAndEnvironmentVariables()
    {
        var s = SampleDrive();
        var matches = CleanupFinder.Find(s, CleanupCatalog.Default(), Expand);
        string[] Paths(string id) => matches.Where(m => m.Entry.Id == id).Select(m => s.GetPath(m.Node)).Order().ToArray();

        Assert.Equal([@"C:\Users\alice\AppData\Local\Temp", @"C:\Users\bob\AppData\Local\Temp"], Paths("user-temp"));
        Assert.Equal([@"C:\Users\alice\AppData\Local\Google\Chrome\User Data\Default\Cache",
                      @"C:\Users\bob\AppData\Local\Google\Chrome\User Data\Default\Cache"], Paths("chrome-cache"));
        Assert.Equal([@"C:\Windows\Temp"], Paths("windows-temp"));
        Assert.Equal([@"C:\Windows\WinSxS"], Paths("winsxs"));
        Assert.Equal([@"C:\Users\alice\src\web\node_modules"], Paths("node-modules")); // the nested one is skipped
    }

    [Fact]
    public void WorksWhenOnlyAFolderInsideWasScanned()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\Users\bob\AppData\Local");
        int temp = Link(s, root, s.Add(root, "Temp", NodeFlags.Directory, 0, 0, 0, 0));
        Link(s, temp, s.Add(temp, "a.tmp", NodeFlags.None, 0, 10, 10, 0));
        s.ComputeTotalsFromRoot();
        var matches = CleanupFinder.Find(s, CleanupCatalog.Default(), Expand);
        Assert.Contains(matches, m => m.Entry.Id == "user-temp" && m.Node == temp);
        Assert.DoesNotContain(matches, m => m.Entry.Id == "windows-temp");
    }

    [Fact]
    public void UserFileReplacesDisablesAndAddsEntries()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(file, """
                // comments and trailing commas are allowed
                { "entries": [
                    { "id": "user-temp", "safety": "caution", "paths": ["{user}\\AppData\\Local\\Temp"], "name": { "en": "Mine" }, },
                    { "id": "winsxs", "disabled": true },
                    { "id": "my-cache", "safety": "safe", "action": "deleteContents", "paths": ["D:\\Cache"], "name": { "cs": "Moje" } },
                ] }
                """);
            var catalog = CleanupCatalog.Load(file, out string? error);
            Assert.Null(error);
            var temp = catalog.Entries.Single(e => e.Id == "user-temp");
            Assert.Equal(CleanupSafety.Caution, temp.Safety);
            Assert.Equal("Mine", CatalogEntry.Text(temp.Name, "cs")); // falls back to English
            Assert.DoesNotContain(catalog.Entries, e => e.Id == "winsxs");
            Assert.Equal(CleanupAction.DeleteContents, catalog.Entries.Single(e => e.Id == "my-cache").Action);

            File.WriteAllText(file, "{ not json");
            var fallback = CleanupCatalog.Load(file, out error);
            Assert.NotNull(error);
            Assert.Contains(fallback.Entries, e => e.Id == "winsxs");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
