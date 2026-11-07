using DiskScope.Core;
using DiskScope.Core.Orphans;
using Xunit.Abstractions;

namespace DiskScope.Tests;

public class OrphanTests(ITestOutputHelper output)
{
    static readonly DateTime Now = new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
    const long MB = 1 << 20;

    static int Link(NodeStore s, int parent, int child)
    {
        s[child].NextSibling = s[parent].FirstChild;
        s[parent].FirstChild = child;
        return child;
    }

    static int Dir(NodeStore s, int parent, string name, long fileSize = 0, int daysOld = 10)
    {
        int d = Link(s, parent, s.Add(parent, name, NodeFlags.Directory, 0, 0, 0, 0));
        if (fileSize > 0)
            Link(s, d, s.Add(d, "data.bin", NodeFlags.None, 0, fileSize, fileSize, Now.AddDays(-daysOld).ToFileTimeUtc()));
        return d;
    }

    static string Expand(string p) => p.Replace("%SystemDrive%", "C:").Replace("%ProgramData%", @"C:\ProgramData")
        .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)").Replace("%ProgramFiles%", @"C:\Program Files");

    [Fact]
    public void ReportsFoldersNoInstalledProgramAccountsFor()
    {
        var s = new NodeStore();
        int root = s.CreateRoot(@"C:\");
        int local = Dir(s, Dir(s, Dir(s, Dir(s, root, "Users"), "me"), "AppData"), "Local");
        Dir(s, local, "Vortex", 50 * MB);                 // matches program "Vortex"
        Dir(s, local, "Black Tree Gaming", 20 * MB);      // matches publisher words
        Dir(s, local, "OldGameStudioX", 300 * MB, 900);   // nothing installed: likely
        Dir(s, local, "SomeCliTool", 40 * MB, 3);         // nothing installed but recently written: possible
        Dir(s, local, "Microsoft", 500 * MB, 900);        // system folder: never
        Dir(s, local, "Tiny", 1 * MB, 900);               // below the size limit
        int npm = Dir(s, local, "npm-cache", 80 * MB, 900); // known to the cleanup catalog
        int packages = Dir(s, local, "Packages");
        Dir(s, packages, "Contoso.App_abc123", 60 * MB);     // store app installed
        Dir(s, packages, "Gone.App_def456", 70 * MB, 5);     // store app removed: likely even if recent
        int pf = Dir(s, root, "Program Files");
        Dir(s, pf, "Git", 200 * MB, 900);                     // install location of an installed program
        Dir(s, pf, "Leftover Suite", 400 * MB, 900);
        s.ComputeTotalsFromRoot();

        var programs = new List<InstalledProgram>
        {
            new("Vortex", "Black Tree Gaming Ltd.", null),
            new("Git", "The Git Development Community", @"C:\Program Files\Git\"),
        };
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contoso.app_ABC123" };

        var found = new OrphanFinder().Find(s, programs, families, [npm], Now, Expand);
        var byName = found.ToDictionary(c => s.GetName(c.Node));

        Assert.Equal(["Gone.App_def456", "Leftover Suite", "OldGameStudioX", "SomeCliTool"], byName.Keys.Order());
        Assert.Equal(OrphanConfidence.Likely, byName["Gone.App_def456"].Confidence);
        Assert.Equal(OrphanKind.StoreApp, byName["Gone.App_def456"].Kind);
        Assert.Equal(OrphanConfidence.Likely, byName["OldGameStudioX"].Confidence);
        Assert.Equal(OrphanConfidence.Possible, byName["SomeCliTool"].Confidence);
        Assert.Equal(OrphanKind.ProgramFiles, byName["Leftover Suite"].Kind);
        Assert.True(byName["OldGameStudioX"].DaysSinceChange >= 900);
        // Likely ones first, larger first.
        Assert.Equal("Leftover Suite", s.GetName(found[0].Node));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_11.2307.4.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe")]
    [InlineData("5319275A.WhatsAppDesktop_2.2623.103.0_neutral_split.scale-150_cv1g1gvanyjgm", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm")]
    public void DerivesPackageFamilyNames(string fullName, string family) => Assert.Equal(family, InstalledPrograms.FamilyName(fullName));

    [Fact]
    public void ReadsThisComputersPrograms()
    {
        var programs = InstalledPrograms.Read();
        var families = InstalledPrograms.ReadStoreFamilies();
        output.WriteLine($"{programs.Count} programs, {families.Count} store families");
        Assert.NotEmpty(programs);
    }
}
