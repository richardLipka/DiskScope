using Microsoft.Win32;

namespace DiskScope.Core;

/// <summary>
/// The "Analyze in DiskScope" entry in Explorer's context menu for folders, drives and folder backgrounds.
/// Registered for the current user only (HKCU), so no administrator rights are needed and the app stays portable.
/// On Windows 11 the entry appears under "Show more options".
/// </summary>
public sealed class ExplorerIntegration(string classesKey = @"Software\Classes")
{
    const string Verb = "DiskScope";

    // Folder, drive, and the empty area inside an open folder (%V is that folder).
    static readonly (string Key, string Argument)[] Targets =
    [
        (@"Directory\shell\" + Verb, "%1"),
        (@"Drive\shell\" + Verb, "%1"),
        (@"Directory\Background\shell\" + Verb, "%V"),
    ];

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{classesKey}\{Targets[0].Key}\command");
            return key != null;
        }
    }

    /// <summary>The executable the registered entry starts, or null.</summary>
    public string? RegisteredExecutable
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{classesKey}\{Targets[0].Key}\command");
            if (key?.GetValue(null) is not string command || !command.StartsWith('"')) return null;
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
    }

    public void Register(string executable, string menuText)
    {
        foreach (var (target, argument) in Targets)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{classesKey}\{target}");
            verb.SetValue(null, menuText);
            verb.SetValue("Icon", $"\"{executable}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{executable}\" --scan \"{argument}\"");
        }
    }

    public void Unregister()
    {
        foreach (var (target, _) in Targets)
            Registry.CurrentUser.DeleteSubKeyTree($@"{classesKey}\{target}", throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Undoes what Explorer's quoting does to a drive root: "C:\" on the command line arrives as C:" because the
    /// backslash escapes the closing quote.
    /// </summary>
    public static string FixPathArgument(string arg)
    {
        string path = arg.Trim().TrimEnd('"');
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }
}
