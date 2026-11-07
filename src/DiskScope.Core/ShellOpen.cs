using System.Diagnostics;

namespace DiskScope.Core;

/// <summary>How to open a file or folder with its default application.</summary>
public static class ShellOpen
{
    /// <summary>
    /// When DiskScope runs elevated (for the fast MFT scan), opening a file directly would start its program with
    /// administrator rights too. Handing it to explorer.exe lets the user's normal, non-elevated shell open it.
    /// </summary>
    public static ProcessStartInfo StartInfo(string path, bool elevated) =>
        elevated
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{path}\"")
            { UseShellExecute = false }
            : new ProcessStartInfo(path) { UseShellExecute = true };

    /// <summary>
    /// Names ending in a dot or a space (possible on NTFS, e.g. created from WSL) are changed by the shell, so
    /// opening "abc." would open "abc". The shell cannot take extended paths, so such items are not opened.
    /// </summary>
    public static bool HasUnsafeEnding(string path) => LongPath.HasUnsafeName(path);
}
