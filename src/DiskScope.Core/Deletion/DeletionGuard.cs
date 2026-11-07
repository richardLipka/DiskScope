using System.IO.Enumeration;

namespace DiskScope.Core.Deletion;

/// <summary>
/// The last line of defence before anything is deleted: refuses drive roots, the system and program folders,
/// user profiles and their main folders, whatever the tree, the catalog or a selection says. Deleting what is
/// inside those folders stays possible, except inside Windows, where only a few known places are allowed.
/// </summary>
public sealed class DeletionGuard(Func<string, string>? expand = null)
{
    readonly Func<string, string> _expand = expand ?? Environment.ExpandEnvironmentVariables;

    /// <summary>Folders that must never be deleted themselves, nor any folder containing them.</summary>
    static readonly string[] Protected =
    [
        "%SystemRoot%", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%", @"%SystemDrive%\Users",
        @"%SystemDrive%\Users\*", @"%SystemDrive%\Users\*\AppData", @"%SystemDrive%\Users\*\AppData\Local",
        @"%SystemDrive%\Users\*\AppData\Roaming", @"%SystemDrive%\Users\*\AppData\LocalLow",
        @"%SystemDrive%\Users\*\Desktop", @"%SystemDrive%\Users\*\Documents", @"%SystemDrive%\Users\*\Downloads",
        @"%SystemDrive%\Users\*\Pictures", @"%SystemDrive%\Users\*\Music", @"%SystemDrive%\Users\*\Videos",
        @"%SystemDrive%\Users\*\OneDrive", @"%SystemDrive%\Users\*\OneDrive - *",
    ];

    /// <summary>The only places inside Windows whose contents may be deleted (the folders themselves stay protected).</summary>
    static readonly string[] AllowedInsideWindows =
    [
        @"%SystemRoot%\Temp\*", @"%SystemRoot%\Minidump\*", @"%SystemRoot%\MEMORY.DMP", @"%SystemRoot%\LiveKernelReports\*",
        @"%SystemRoot%\SoftwareDistribution\Download\*", @"%SystemRoot%\Logs\*",
    ];

    public enum Verdict
    {
        Allowed,
        DriveRoot,
        ProtectedFolder,
        InsideWindows,
    }

    /// <summary>
    /// Checks the path as given and, when it exists, also where it really leads: a junction or symbolic link in a
    /// folder above it could otherwise make a protected folder appear under an innocent name. The item itself is not
    /// resolved, so deleting a link (which removes only the link) stays possible.
    /// </summary>
    public Verdict CheckResolved(string path) => CheckResolved(path, null);

    /// <summary>
    /// As <see cref="CheckResolved(string)"/>, remembering each parent folder's real path in <paramref name="cache"/>:
    /// the files of one folder (often thousands at once) then cost one lookup instead of one each.
    /// </summary>
    public Verdict CheckResolved(string path, Dictionary<string, string?>? cache)
    {
        var verdict = Check(path);
        if (verdict != Verdict.Allowed) return verdict;
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\'));
        if (parent == null) return verdict;
        if (cache == null || !cache.TryGetValue(parent, out string? real))
        {
            real = Native.NativeMethods.FinalPath(parent);
            if (cache != null) cache[parent] = real;
        }
        if (real == null) return verdict;
        return Check(Path.Combine(StripPrefix(real), Path.GetFileName(path.TrimEnd('\\'))));
    }

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\server\share" → "\\server\share".</summary>
    static string StripPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..]
        : path;

    public Verdict Check(string path)
    {
        string full = StripPrefix(Path.GetFullPath(StripPrefix(path))).TrimEnd('\\');
        string[] target = Split(full);
        if (target.Length <= 1) return Verdict.DriveRoot;
        // \\server\share is the root of a network drive.
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && target.Length <= 2) return Verdict.DriveRoot;

        foreach (string pattern in Protected)
        {
            string[] p = Split(_expand(pattern));
            // Refused when the path is the protected folder or contains it (is a prefix of the pattern).
            if (target.Length <= p.Length && SegmentsMatch(p, target, target.Length)) return Verdict.ProtectedFolder;
        }

        string[] windows = Split(_expand("%SystemRoot%"));
        if (target.Length > windows.Length && SegmentsMatch(windows, target, windows.Length))
        {
            foreach (string allowed in AllowedInsideWindows)
            {
                string[] a = Split(_expand(allowed));
                if (a[^1] == "*" ? target.Length >= a.Length && SegmentsMatch(a, target, a.Length - 1)
                                 : target.Length == a.Length && SegmentsMatch(a, target, a.Length))
                    return Verdict.Allowed;
            }
            return Verdict.InsideWindows;
        }
        return Verdict.Allowed;
    }

    static string[] Split(string path) => path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Compares the first <paramref name="count"/> segments, wildcards in the pattern allowed.</summary>
    static bool SegmentsMatch(string[] pattern, string[] path, int count)
    {
        for (int i = 0; i < count; i++)
        {
            bool wild = pattern[i].Contains('*') || pattern[i].Contains('?');
            if (wild ? !FileSystemName.MatchesSimpleExpression(pattern[i], path[i], ignoreCase: true)
                     : !pattern[i].Equals(path[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
