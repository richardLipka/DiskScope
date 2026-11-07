using DiskScope.Core.Cleanup;

namespace DiskScope.Core.Orphans;

public enum OrphanConfidence
{
    /// <summary>No installed program matches and nothing inside has changed for a long time.</summary>
    Likely,
    /// <summary>No installed program matches, but something still writes there (a portable or command-line tool?).</summary>
    Possible,
}

public enum OrphanKind
{
    /// <summary>A Microsoft Store app's data folder whose app is not installed.</summary>
    StoreApp,
    ProgramFiles,
    AppData,
    ProgramData,
}

public sealed record OrphanCandidate(int Node, OrphanKind Kind, OrphanConfidence Confidence, int DaysSinceChange);

/// <summary>
/// Finds folders in the usual application data locations that no installed program accounts for.
/// It is a heuristic: names are compared loosely with program names and publishers, so the result is a list
/// of candidates to review, not a verdict.
/// </summary>
public sealed class OrphanFinder
{
    /// <summary>Folders below this size are not reported.</summary>
    public long MinSize { get; init; } = 10L << 20;

    /// <summary>Without a matching program and unchanged this long, a folder counts as likely abandoned.</summary>
    public int StaleDays { get; init; } = 180;

    static readonly (string Pattern, OrphanKind Kind)[] Locations =
    [
        (@"{user}\AppData\Local\Packages\*", OrphanKind.StoreApp),
        (@"{user}\AppData\Local\*", OrphanKind.AppData),
        (@"{user}\AppData\Roaming\*", OrphanKind.AppData),
        (@"{user}\AppData\LocalLow\*", OrphanKind.AppData),
        (@"%ProgramData%\*", OrphanKind.ProgramData),
        (@"%ProgramFiles%\*", OrphanKind.ProgramFiles),
        (@"%ProgramFiles(x86)%\*", OrphanKind.ProgramFiles),
    ];

    /// <summary>Folders that belong to Windows itself, or hold data of many apps; never reported.</summary>
    static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "PeerDistRepub",
        "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "Comms", "History", "Application Data", "Windows",
        "Microsoft_Corporation", "MicrosoftEdge", "SquirrelTemp", "Package Cache", "USOPrivate", "USOShared", "ssh",
        "SoftwareDistribution", "Windows App Certification Kit", "WindowsHolographicDevices", "dbg", "Desktop", "Documents",
        "Start Menu", "Templates", "Common Files", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "ModifiableWindowsApps", "Internet Explorer",
        "Microsoft Update Health Tools", "Reference Assemblies", "MSBuild", "Microsoft.NET", "Uninstall Information",
        "Microsoft OneDrive", "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Realtek", "regid.1991-06.com.microsoft",
        "Speech", "Google", "Mozilla", "Apple", "Adobe", "Sun", "Oracle",
        // Microsoft components whose folder names do not match their names in the programs list.
        "dotnet", "WSL", "Windows Kits", "Microsoft SDKs", "Microsoft Visual Studio", "Microsoft Office", "IIS", "IIS Express",
        "Microsoft SQL Server", "Microsoft Analysis Services", "PackageManagement", "Microsoft Help", "Windows Identity Foundation",
    };

    public List<OrphanCandidate> Find(NodeStore store, IReadOnlyList<InstalledProgram> programs, IReadOnlySet<string> storeFamilies,
        IEnumerable<int> knownLocations, DateTime nowUtc, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var index = new ProgramIndex(programs);
        var known = knownLocations.ToHashSet();
        var seen = new HashSet<int>();
        var result = new List<OrphanCandidate>();
        foreach (var (pattern, kind) in Locations)
        {
            // Without the list of installed Store apps every Store app's data would look abandoned: skip them.
            if (kind == OrphanKind.StoreApp && storeFamilies.Count == 0)
            {
                // Still mark the folders as seen, so the AppData pass below does not report them either.
                foreach (int node in CleanupFinder.ResolvePath(store, CleanupFinder.ExpandPattern(pattern, expand))) seen.Add(node);
                continue;
            }
            foreach (int node in CleanupFinder.ResolvePath(store, CleanupFinder.ExpandPattern(pattern, expand)))
            {
                if (!seen.Add(node) || !store.IsLive(node) || !store[node].IsDirectory) continue;
                if (store[node].Size < MinSize) continue;
                string name = store.GetName(node);
                if (SystemFolders.Contains(name)) continue;
                // Places the cleanup catalog knows about are covered there, with better advice.
                if (ContainsAny(store, node, known)) continue;

                bool orphan = kind == OrphanKind.StoreApp
                    ? !storeFamilies.Contains(name)
                    : !index.Matches(name, store.GetPath(node));
                if (!orphan) continue;

                int days = store[node].MTime > 0 ? (int)(nowUtc - DateTime.FromFileTimeUtc(store[node].MTime)).TotalDays : int.MaxValue;
                var confidence = kind == OrphanKind.StoreApp || days >= StaleDays ? OrphanConfidence.Likely : OrphanConfidence.Possible;
                result.Add(new OrphanCandidate(node, kind, confidence, days));
            }
        }
        return result.OrderBy(c => c.Confidence).ThenByDescending(c => store[c.Node].Size).ToList();
    }

    static bool ContainsAny(NodeStore store, int folder, HashSet<int> nodes)
    {
        if (nodes.Contains(folder)) return true;
        foreach (int n in nodes)
            for (int p = store[n].Parent; p != NodeStore.None; p = store[p].Parent)
                if (p == folder) return true;
        return false;
    }

    /// <summary>Loose matching of folder names against installed programs' names, publishers and install folders.</summary>
    sealed class ProgramIndex
    {
        readonly List<string> _names = [];
        readonly HashSet<string> _words = [];
        readonly List<string> _locations = [];

        public ProgramIndex(IReadOnlyList<InstalledProgram> programs)
        {
            foreach (var p in programs)
            {
                foreach (string text in new[] { p.Name, p.Publisher ?? "" })
                {
                    string n = Normalize(text);
                    if (n.Length >= 3) _names.Add(n);
                    // Single words catch "Black Tree Gaming Ltd." → folder "Black Tree Gaming" or "Vortex".
                    foreach (string w in text.Split([' ', '.', ',', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
                        if (Normalize(w) is { Length: >= 4 } word && !CommonWords.Contains(word)) _words.Add(word);
                }
                if (!string.IsNullOrWhiteSpace(p.InstallLocation))
                    _locations.Add(p.InstallLocation.Trim().Trim('"').TrimEnd('\\'));
            }
        }

        static readonly HashSet<string> CommonWords =
        [
            "microsoft", "corporation", "software", "systems", "games", "studio", "studios", "technologies", "update", "tools",
            "version", "runtime", "service", "services", "windows", "driver", "drivers", "redistributable", "package", "edition",
            "limited", "incorporated", "community", "foundation", "development", "team", "group", "digital", "interactive",
        ];

        public bool Matches(string folderName, string folderPath)
        {
            foreach (string loc in _locations)
                if (loc.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) &&
                    (loc.Length == folderPath.Length || loc[folderPath.Length] == '\\'))
                    return true;

            string f = Normalize(folderName);
            if (f.Length < 3) return true; // too short to judge: do not report
            foreach (string n in _names)
                if (n.Contains(f) || (n.Length >= 4 && f.Contains(n))) return true;
            foreach (string w in _words)
                if (f.Contains(w)) return true;
            return false;
        }

        static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
