using DiskScope.UI;

namespace DiskScope;

static class Program
{
    /// <summary>
    /// Command line: [--path folder] [--scan folder] [--lang cs|en]
    /// Test hook: --screenshot file.png [--expand levels] captures the window after the scan and exits.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        L.Lang = Arg("--lang") ?? settings.Language;
        string? scan = Arg("--scan");
        var form = new MainForm(settings, scan ?? Arg("--path"), autoScan: scan != null);
        if (Arg("--screenshot") is string shot)
            TestHooks.CaptureAfterScan(form, shot, int.TryParse(Arg("--expand"), out int levels) ? levels : 0);
        Application.Run(form);
    }
}
