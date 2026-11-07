#if DEBUG
using DiskScope.Core;

namespace DiskScope.UI;

/// <summary>
/// Lets automated checks run the real UI: scan, act, save a screenshot or a report, quit. Debug builds only.
/// Command line, all run after the scan (or after the last saved scan is opened) and then exit:
///   --screenshot file.png [--expand levels] [--menu]                    captures the window (with the context menu open)
///   --data-manager file.png                                             captures the saved-data window
///   --scan folder --compare-test file.png                               compares with the previous saved scan, captures
///   --search-test file.png                                              opens the search panel (largest files), captures
///   --ai-prompt path --out file.txt                                     writes the AI question for a path
///   --cleanup-test file.png [--cleanup-mark]                            opens the cleanup panel (selects caches), captures
///   --orphan-test file.png                                              opens the abandoned-data panel, captures
///   --scan folder --duplicate-test file.png [--duplicate-folder path]   searches duplicates (in a folder), captures
///   --map-test file.png                                                 opens the treemap, captures
///   --scan folder --queue-test file.png                                 queues items in a paused queue, captures, cancels
///   --delete-test report.txt [--delete-delay ms]                        deletes test items and writes a report
///   --scan folder --empty-test subfolder --out report.txt [--shot file.png]   empties a folder through the queue, times it
///   --messages file.txt                                               with any of the above: error messages shown, one per line
/// </summary>
static class TestHooks
{
    public static void Install(MainForm form, string[] args, bool scanning)
    {
        string? Arg(string name) => Program.Arg(args, name);
        // Dialogs would block an unattended run: collect their texts instead (written by --messages file.txt).
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a.EndsWith("-test", StringComparison.Ordinal) || a == "--screenshot"))
        {
            form.TestMessages = [];
            if (Arg("--messages") is string messages)
                form.FormClosed += (_, _) => File.WriteAllLines(messages, form.TestMessages);
        }
        if (Arg("--screenshot") is string shot)
            CaptureAfterScan(form, shot, int.TryParse(Arg("--expand"), out int levels) ? levels : 0, args.Contains("--menu"), afterLoad: !scanning);
        if (Arg("--compare-test") is string cmpShot)
            CaptureCompare(form, cmpShot, int.TryParse(Arg("--expand"), out int cmpLevels) ? cmpLevels : 0);
        if (Arg("--search-test") is string searchShot) CaptureSearch(form, searchShot);
        if (Arg("--map-test") is string mapShot) CaptureMap(form, mapShot);
        if (Arg("--duplicate-test") is string dupShot) CaptureDuplicates(form, dupShot, Arg("--duplicate-folder"));
        if (Arg("--orphan-test") is string orphanShot) CaptureOrphans(form, orphanShot);
        if (Arg("--queue-test") is string queueShot) CaptureQueue(form, queueShot);
        if (Arg("--cleanup-test") is string cleanupShot) CaptureCleanup(form, cleanupShot, args.Contains("--cleanup-mark"));
        if (Arg("--ai-prompt") is string aiPath && Arg("--out") is string aiOut) WriteAiPrompt(form, aiPath, aiOut);
        if (Arg("--data-manager") is string dmShot) CaptureDataManager(form, dmShot);
        if (Arg("--empty-test") is string emptyFolder && Arg("--out") is string emptyReport)
            RunEmptyTest(form, emptyFolder, emptyReport, Arg("--shot"));
        if (Arg("--delete-test") is string report)
            RunDeleteTest(form, report, int.TryParse(Arg("--delete-delay"), out int delay) ? delay : 0);
    }

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

    /// <summary>After the scan: pauses the queue, queues every top-level item, captures the queue panel, cancels, quits.</summary>
    public static void CaptureQueue(MainForm form, string file)
    {
        form.ScanCompleted += () =>
        {
            form.QueueAllPausedForTest();
            After(800, () =>
            {
                Capture(form, file);
                form.CancelQueueForTest();
                form.Close();
            });
        };
    }

    /// <summary>When the tree is ready, opens the map of the root, captures it, quits.</summary>
    public static void CaptureMap(MainForm form, string file)
    {
        Action open = () => form.ShowMapForTest();
        form.ScanCompleted += open;
        form.SnapshotLoaded += open;
        form.MapPanelForTest.Shown += () => After(1000, () =>
        {
            Capture(form, file);
            form.Close();
        });
    }

    /// <summary>After the scan, opens the duplicates panel, runs the search, captures the result, quits.</summary>
    public static void CaptureDuplicates(MainForm form, string file, string? folder)
    {
        form.ScanCompleted += () =>
        {
            if (folder != null) form.FindDuplicatesInForTest(folder);
            else
            {
                form.ShowDuplicatesForTest();
                form.DuplicatePanelForTest.Start();
            }
        };
        form.DuplicatePanelForTest.Finished += () => After(800, () =>
        {
            Capture(form, file);
            form.Close();
        });
    }

    /// <summary>When the tree is ready, opens the abandoned-data panel, captures it, quits.</summary>
    public static void CaptureOrphans(MainForm form, string file)
    {
        Action open = () => form.ShowOrphansForTest();
        form.ScanCompleted += open;
        form.SnapshotLoaded += open;
        form.OrphanPanelForTest.Found += () => After(800, () =>
        {
            Capture(form, file);
            form.Close();
        });
    }

    /// <summary>
    /// After the scan, empties one folder through the delete queue and measures the longest stall of the UI thread
    /// while the queue works (a 50 ms timer that should keep ticking), then writes a report and quits.
    /// </summary>
    public static void RunEmptyTest(MainForm form, string folder, string report, string? shot)
    {
        form.ScanCompleted += () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var gapClock = System.Diagnostics.Stopwatch.StartNew();
            long maxGap = 0;
            int queued = form.EmptyFolderForTest(folder);
            long enqueueMs = clock.ElapsedMilliseconds;
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                maxGap = Math.Max(maxGap, gapClock.ElapsedMilliseconds);
                gapClock.Restart();
                if (!form.QueueIdleForTest || clock.ElapsedMilliseconds < 500) return;
                timer.Stop();
                var store = form.Store!;
                int node = store.FindPath(folder);
                var lines = new List<string>
                {
                    $"queued {queued} in {enqueueMs} ms",
                    $"done in {clock.ElapsedMilliseconds} ms, longest UI stall {maxGap} ms",
                    $"tree: folder files {(node == NodeStore.None ? -1 : store[node].FileCount)}, root files {store[store.Root].FileCount}",
                    $"disk: files left {Directory.EnumerateFiles(folder).Count()}",
                    form.QueueSummaryForTest,
                };
                File.WriteAllLines(report, lines);
                if (shot != null) Capture(form, shot);
                form.Close();
            };
            timer.Start();
        };
    }

    /// <summary>When the tree is ready, opens the cleanup panel, captures it with the first item selected, quits.</summary>
    public static void CaptureCleanup(MainForm form, string file, bool markCaches)
    {
        Action open = () => form.ShowCleanupForTest();
        form.ScanCompleted += open;
        form.SnapshotLoaded += open;
        form.CleanupPanelForTest.CleanupFound += () => After(800, () =>
        {
            if (markCaches) form.CleanupPanelForTest.MarkRedownloadableForTest();
            After(300, () =>
            {
                Capture(form, file);
                form.Close();
            });
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
    public static void RunDeleteTest(MainForm form, string report, int delayMs)
    {
        // The delay leaves time to break the disk (e.g. remove a subst drive) between the scan and the deleting.
        form.ScanCompleted += () => After(Math.Max(1, delayMs), () =>
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
                lines.AddRange(form.QueueReportForTest());
            }
            catch (Exception ex)
            {
                lines.Add("ERROR " + ex);
            }
            File.WriteAllLines(report, lines);
            form.Close();
        });
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
#endif
