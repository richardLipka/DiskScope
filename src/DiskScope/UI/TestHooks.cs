using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>Lets automated checks run the real UI: scan, act, save a screenshot or a report, quit.</summary>
static class TestHooks
{
    public static void CaptureAfterScan(MainForm form, string file, int expandLevels, bool withMenu)
    {
        form.ScanCompleted += () =>
        {
            form.ExpandLargest(expandLevels);
            if (withMenu) form.ShowMenuAtFocused();
            After(800, () =>
            {
                Capture(form, file, withMenu);
                form.Close();
            });
        };
    }

    /// <summary>
    /// Expects a folder holding "recycle.bin" and "permanent\" (with files inside): deletes the first to the
    /// recycle bin and the second permanently through the app's own code path, then writes a report.
    /// </summary>
    public static void RunDeleteTest(MainForm form, string report)
    {
        form.ScanCompleted += () =>
        {
            var lines = new List<string>();
            try
            {
                var store = form.Store!;
                long before = store[store.Root].Size;
                int recycle = store.FindPath(Path.Combine(store.RootPath, "recycle.bin"));
                int permanent = store.FindPath(Path.Combine(store.RootPath, "permanent"));
                lines.Add($"found recycle={recycle != NodeStore.None} permanent={permanent != NodeStore.None} size={before}");
                var failed1 = form.PerformDelete([recycle], permanent: false);
                var failed2 = form.PerformDelete([permanent], permanent: true);
                lines.Add($"failed={failed1.Count + failed2.Count}");
                lines.Add($"exists recycle={File.Exists(Path.Combine(store.RootPath, "recycle.bin"))} permanent={Directory.Exists(Path.Combine(store.RootPath, "permanent"))}");
                lines.Add($"tree recycle={store.FindPath(Path.Combine(store.RootPath, "recycle.bin")) != NodeStore.None} permanent={store.FindPath(Path.Combine(store.RootPath, "permanent")) != NodeStore.None}");
                lines.Add($"size after={store[store.Root].Size} files={store[store.Root].FileCount} dirs={store[store.Root].DirCount}");
            }
            catch (Exception ex)
            {
                lines.Add("ERROR " + ex);
            }
            File.WriteAllLines(report, lines);
            form.Close();
        };
    }

    static void After(int ms, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = ms };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    public static void Capture(Form form, string file, bool wholeScreen = false)
    {
        var bounds = wholeScreen ? Screen.FromControl(form).Bounds : form.RectangleToScreen(form.ClientRectangle);
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
