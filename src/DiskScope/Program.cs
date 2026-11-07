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
    ///   --scan folder --compare-test file.png               compares with the previous saved scan, captures
    ///   --search-test file.png                              opens the search panel (largest files), captures
    ///   --ai-prompt path --out file.txt                     writes the AI question for a path
    ///   --cleanup-test file.png                             opens the cleanup panel, captures
    ///   --scan folder --queue-test file.png                 queues items in a paused queue, captures, cancels
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
        string? scan = Arg("--scan") is string s ? Core.ExplorerIntegration.FixPathArgument(s) : null;
        string? path = Arg("--path") is string p ? Core.ExplorerIntegration.FixPathArgument(p) : null;
        var form = new MainForm(settings, scan ?? path, autoScan: scan != null);
        if (Arg("--screenshot") is string shot)
            TestHooks.CaptureAfterScan(form, shot, int.TryParse(Arg("--expand"), out int levels) ? levels : 0,
                args.Contains("--menu"), afterLoad: scan == null);
        if (Arg("--compare-test") is string cmpShot)
            TestHooks.CaptureCompare(form, cmpShot, int.TryParse(Arg("--expand"), out int cmpLevels) ? cmpLevels : 0);
        if (Arg("--search-test") is string searchShot)
            TestHooks.CaptureSearch(form, searchShot);
        if (Arg("--queue-test") is string queueShot)
            TestHooks.CaptureQueue(form, queueShot);
        if (Arg("--cleanup-test") is string cleanupShot)
            TestHooks.CaptureCleanup(form, cleanupShot);
        if (Arg("--ai-prompt") is string aiPath && Arg("--out") is string aiOut)
            TestHooks.WriteAiPrompt(form, aiPath, aiOut);
        if (Arg("--data-manager") is string dmShot)
            TestHooks.CaptureDataManager(form, dmShot);
        if (Arg("--delete-test") is string report)
            TestHooks.RunDeleteTest(form, report);
        Application.Run(form);
    }
}
