using DiskScope.UI;

namespace DiskScope;

static class Program
{
    /// <summary>Command line: [--path folder] [--scan folder] [--lang cs|en]</summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError(e.ExceptionObject as Exception, fatal: true);

        var settings = AppSettings.Load();
        L.Lang = Arg(args, "--lang") ?? settings.Language;
        string? scan = Arg(args, "--scan") is string s ? Core.ExplorerIntegration.FixPathArgument(s) : null;
        string? path = Arg(args, "--path") is string p ? Core.ExplorerIntegration.FixPathArgument(p) : null;
        var form = new MainForm(settings, scan ?? path, autoScan: scan != null);
#if DEBUG
        // Test hooks can delete files and write anywhere, so release builds do not contain them.
        TestHooks.Install(form, args, scan != null);
#endif
        Application.Run(form);
    }

    internal static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Logs an unexpected error to data\error.log and tells the user, instead of the process just vanishing.</summary>
    static void ReportError(Exception? ex, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "error.log"), $"{DateTime.Now:O} {(fatal ? "FATAL" : "ERROR")} {ex}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more can be done if even the log cannot be written.
        }
        MessageBox.Show(L.T(fatal ? "ErrorFatal" : "ErrorUnexpected", ex?.Message ?? "?", Path.Combine(AppPaths.DataDir, "error.log")),
            "DiskScope", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
