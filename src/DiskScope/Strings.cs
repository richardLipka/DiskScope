using System.Globalization;

namespace DiskScope;

/// <summary>Czech and English UI texts. Switching the language raises <see cref="Changed"/>.</summary>
static class L
{
    public static readonly string[] Languages = ["cs", "en"];

    static string _lang = "cs";

    public static event Action? Changed;

    public static string Lang
    {
        get => _lang;
        set
        {
            if (!Languages.Contains(value) || value == _lang) return;
            _lang = value;
            Culture = CultureInfo.GetCultureInfo(value == "cs" ? "cs-CZ" : "en-US");
            Changed?.Invoke();
        }
    }

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("cs-CZ");

    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "cs" or "sk" ? "cs" : "en";

    public static string T(string key, params object[] args)
    {
        string s = Texts.TryGetValue(key, out var t) ? (_lang == "cs" ? t.Cs : t.En) : key;
        return args.Length == 0 ? s : string.Format(Culture, s, args);
    }

    static readonly Dictionary<string, (string Cs, string En)> Texts = new()
    {
        ["AppTitle"] = ("DiskScope – analýza a čištění disku", "DiskScope – disk space analyzer"),
        ["Scan"] = ("Skenovat", "Scan"),
        ["Stop"] = ("Zastavit", "Stop"),
        ["Browse"] = ("Složka…", "Folder…"),
        ["BrowseTip"] = ("Vybrat složku ke skenování", "Choose a folder to scan"),
        ["Up"] = ("Nahoru", "Up"),
        ["UpTip"] = ("O úroveň výš (Backspace)", "Up one level (Backspace)"),
        ["Language"] = ("Jazyk", "Language"),
        ["RunAsAdmin"] = ("Spustit jako správce", "Run as administrator"),
        ["RunAsAdminTip"] = ("Rychlý režim MFT pro NTFS disky vyžaduje práva správce",
                             "The fast MFT mode for NTFS drives needs administrator rights"),
        ["DriveItem"] = ("{0}  {1} ({2})  volno {3} z {4}", "{0}  {1} ({2})  {3} free of {4}"),
        ["LocalDisk"] = ("Místní disk", "Local Disk"),
        ["ColName"] = ("Název", "Name"),
        ["ColOnDisk"] = ("Na disku", "On disk"),
        ["ColPercent"] = ("% nadřazené", "% of parent"),
        ["ColSize"] = ("Velikost", "Size"),
        ["ColFiles"] = ("Soubory", "Files"),
        ["ColFolders"] = ("Složky", "Folders"),
        ["ColModified"] = ("Změněno", "Modified"),
        ["ColDelta"] = ("Změna", "Change"),
        ["Ready"] = ("Vyberte disk nebo složku a spusťte sken.", "Choose a drive or folder and start a scan."),
        ["Scanning"] = ("Skenuji", "Scanning"),
        ["ScanDone"] = ("Hotovo", "Done"),
        ["ScanCancelled"] = ("Zastaveno", "Stopped"),
        ["ScanFailed"] = ("Chyba skenu: {0}", "Scan failed: {0}"),
        ["StatusLine"] = ("{0} · {1} souborů · {2} složek · {3} · {4} · chyby přístupu: {5}",
                          "{0} · {1} files · {2} folders · {3} · {4} · access errors: {5}"),
        ["MftProgress"] = ("MFT {0} %", "MFT {0} %"),
        ["ModeMft"] = ("Režim: MFT (rychlý)", "Mode: MFT (fast)"),
        ["ModeDir"] = ("Režim: procházení složek", "Mode: folder enumeration"),
        ["Memory"] = ("paměť {0}", "memory {0}"),
        ["DetailFile"] = ("{0}    na disku {1} · velikost {2} · změněno {3}", "{0}    on disk {1} · size {2} · modified {3}"),
        ["DetailDir"] = ("{0}    na disku {1} · velikost {2} · {3} souborů · {4} složek",
                         "{0}    on disk {1} · size {2} · {3} files · {4} folders"),
        ["NotScanned"] = ("…", "…"),
        ["AccessDenied"] = ("(nepřístupné)", "(access denied)"),
        ["Link"] = ("(odkaz)", "(link)"),
        ["ConfirmStopScan"] = ("Sken stále běží. Opravdu ho zastavit?", "A scan is still running. Stop it?"),
    };
}
