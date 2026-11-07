using Microsoft.Win32;

namespace DiskScope.Core.Orphans;

public sealed record InstalledProgram(string Name, string? Publisher, string? InstallLocation);

/// <summary>What is installed on this computer, read from the registry (no administrator rights needed).</summary>
public static class InstalledPrograms
{
    static readonly (RegistryKey Hive, string Path)[] UninstallKeys =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    /// <summary>Programs listed in "Apps &amp; features" (classic installers).</summary>
    public static List<InstalledProgram> Read()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, path) in UninstallKeys)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    // One unreadable entry must not lose the whole list (a shorter list means more false "abandoned").
                    try
                    {
                        using var key = root.OpenSubKey(sub);
                        if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                        list.Add(new InstalledProgram(name, key.GetValue("Publisher") as string, key.GetValue("InstallLocation") as string));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return list;
    }

    const string PackageRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Package family names (Name_PublisherId) of Microsoft Store apps installed for the current user.</summary>
    public static HashSet<string> ReadStoreFamilies()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(PackageRepository);
            if (root == null) return families;
            foreach (string fullName in root.GetSubKeyNames())
                if (FamilyName(fullName) is string family) families.Add(family);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An empty set makes the finder skip Store app data rather than call all of it abandoned.
        }
        return families;
    }

    /// <summary>"Name_Version_Arch_ResourceId_PublisherId" → "Name_PublisherId".</summary>
    public static string? FamilyName(string packageFullName)
    {
        int first = packageFullName.IndexOf('_');
        int last = packageFullName.LastIndexOf('_');
        return first > 0 && last > first ? packageFullName[..first] + packageFullName[last..] : null;
    }
}
