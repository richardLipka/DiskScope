using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>Lets automated checks run the real UI: scan, act, save a screenshot or a report, quit.</summary>
static class TestHooks
{
    public static void CaptureAfterScan(MainForm form, string file, int expandLevels, bool withMenu, bool afterLoad)
    {
        Action capture = () =>
        {
            form.ExpandLargest(expandLevels);
            if (withMenu) form.ShowMenuAtFocused();
            After(800, () =>
            {
                Capture(form, file, withMenu);
                form.Close();
            });
        };
        if (afterLoad) form.SnapshotLoaded += capture;
        else form.ScanCompleted += capture;
    }

    /// <summary>After the scan, compares with the previous saved scan (only grown items), captures, quits.</summary>
    public static void CaptureCompare(MainForm form, string file, int expandLevels)
    {
        form.ScanCompleted += () => form.CompareWithPreviousForTest(onlyGrown: true);
        form.CompareStarted += () =>
        {
            form.ExpandLargest(expandLevels);
            After(800, () =>
            {
                Capture(form, file);
                form.Close();
            });
        };
    }

    /// <summary>When the tree is ready (scan done or saved scan opened), opens the search panel, captures the results, quits.</summary>
    public static void CaptureSearch(MainForm form, string file)
    {
        Action open = () => form.ShowSearchForTest();
        form.ScanCompleted += open;
        form.SnapshotLoaded += open;
        form.SearchPanelForTest.SearchCompleted += () => After(800, () =>
        {
            Capture(form, file);
            form.Close();
        });
    }

    /// <summary>When the tree is ready, writes the AI question for one path to a file and quits.</summary>
    public static void WriteAiPrompt(MainForm form, string path, string file)
    {
        Action write = () =>
        {
            var store = form.Store!;
            int node = store.FindPath(path);
            File.WriteAllText(file, node == NodeStore.None ? "NOT FOUND " + path : AiPrompt.Build(store, node));
            form.Close();
        };
        form.ScanCompleted += write;
        form.SnapshotLoaded += write;
    }

    /// <summary>Opens the data manager, captures it, and quits.</summary>
    public static void CaptureDataManager(MainForm form, string file)
    {
        form.Shown += (_, _) => After(300, () =>
        {
            After(1000, () =>
            {
                var dlg = Application.OpenForms.OfType<DataManagerForm>().FirstOrDefault();
                if (dlg != null)
                {
                    Capture(dlg, file);
                    dlg.Close();
                }
                form.Close();
            });
            form.ShowDataManagerForTest();
        });
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    /// <summary>
    /// Renders the window itself (PrintWindow), so other windows on top of it do not end up in the picture.
    /// With <paramref name="withPopups"/> the screen area is copied instead, to include an open context menu.
    /// </summary>
    public static void Capture(Form form, string file, bool withPopups = false)
    {
        var client = form.RectangleToScreen(form.ClientRectangle);
        using var bmp = new Bitmap(client.Width, client.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            if (withPopups) g.CopyFromScreen(client.Location, Point.Empty, client.Size);
            else
            {
                using var whole = new Bitmap(form.Width, form.Height);
                using (var wg = Graphics.FromImage(whole))
                {
                    nint hdc = wg.GetHdc();
                    PrintWindow(form.Handle, hdc, 2); // PW_RENDERFULLCONTENT
                    wg.ReleaseHdc(hdc);
                }
                g.DrawImage(whole, new Point(form.Left - client.Left, form.Top - client.Top));
            }
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
