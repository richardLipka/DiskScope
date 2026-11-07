using DiskScope.UI;

namespace DiskScope;

static class Program
{
    /// <summary>
    /// Command line: [--path folder] [--scan folder] [--lang cs|en]
    /// Test hooks, run after the scan and then exit:
    ///   --screenshot file.png [--expand levels] [--menu]   captures the window (with the context menu open);
    ///                                                       without --scan, after the last saved scan is opened
    ///   --data-manager file.png                             captures the saved-data window
    ///   --delete-test report.txt                            deletes test items and writes a report
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
            TestHooks.CaptureAfterScan(form, shot, int.TryParse(Arg("--expand"), out int levels) ? levels : 0,
                args.Contains("--menu"), afterLoad: scan == null);
        if (Arg("--data-manager") is string dmShot)
            TestHooks.CaptureDataManager(form, dmShot);
        if (Arg("--delete-test") is string report)
            TestHooks.RunDeleteTest(form, report);
        Application.Run(form);
    }
}
