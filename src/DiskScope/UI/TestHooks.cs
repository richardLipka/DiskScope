namespace DiskScope.UI;

/// <summary>Lets automated checks run the real UI: scan, expand, save a screenshot, quit.</summary>
static class TestHooks
{
    public static void CaptureAfterScan(MainForm form, string file, int expandLevels)
    {
        form.ScanCompleted += () =>
        {
            form.ExpandLargest(expandLevels);
            var timer = new System.Windows.Forms.Timer { Interval = 800 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Capture(form, file);
                form.Close();
            };
            timer.Start();
        };
    }

    public static void Capture(Form form, string file)
    {
        form.Activate();
        var bounds = form.RectangleToScreen(form.ClientRectangle);
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
