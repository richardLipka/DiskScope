namespace DiskScope.UI;

/// <summary>Light or dark appearance, chosen in the settings menu.</summary>
enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Colors of the custom-drawn parts (bars, change column, glyphs). Standard controls follow Windows Forms' own color
/// mode, which <see cref="Apply"/> sets before any window exists.
/// </summary>
static class Theme
{
#pragma warning disable WFO5001 // the color mode API is marked experimental, but is the supported way to get dark controls
    public static void Apply(ThemeMode mode) => Application.SetColorMode(mode switch
    {
        ThemeMode.Light => SystemColorMode.Classic,
        ThemeMode.Dark => SystemColorMode.Dark,
        _ => SystemColorMode.System,
    });

    public static bool IsDark => Application.IsDarkModeEnabled;
#pragma warning restore WFO5001

    public static Color Bar => IsDark ? Color.FromArgb(58, 108, 168) : Color.FromArgb(132, 178, 235);
    public static Color BarTrack => IsDark ? Color.FromArgb(52, 56, 62) : Color.FromArgb(232, 238, 246);
    public static Color BarText => IsDark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(30, 30, 30);
    public static Color Grow => IsDark ? Color.FromArgb(240, 113, 98) : Color.FromArgb(192, 57, 43);
    public static Color Shrink => IsDark ? Color.FromArgb(96, 204, 128) : Color.FromArgb(39, 136, 76);
    public static Color InactiveSelection => IsDark ? Color.FromArgb(68, 68, 72) : Color.FromArgb(217, 217, 217);
    public static Color Glyph => IsDark ? Color.FromArgb(175, 175, 175) : Color.FromArgb(110, 110, 110);

    /// <summary>
    /// Draws a list view column header. Owner-drawn lists keep the light header style even in dark mode, which
    /// leaves grey text on a dark background; in dark mode the header is drawn here instead.
    /// </summary>
    /// <param name="sortDescending">Draws a sort arrow when not null.</param>
    public static void DrawHeader(DrawListViewColumnHeaderEventArgs e, Font font, bool? sortDescending = null)
    {
        if (!IsDark)
        {
            e.DrawDefault = true;
            return;
        }
        var g = e.Graphics;
        var b = e.Bounds;
        using (var back = new SolidBrush(Color.FromArgb(43, 43, 46))) g.FillRectangle(back, b);
        using (var line = new Pen(Color.FromArgb(72, 72, 76)))
        {
            g.DrawLine(line, b.Right - 1, b.Top + 4, b.Right - 1, b.Bottom - 4);
            g.DrawLine(line, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
        }
        var align = e.Header?.TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        var text = Rectangle.Inflate(b, -6, 0);
        if (sortDescending != null)
        {
            TextRenderer.DrawText(g, sortDescending.Value ? "▾" : "▴", font, b with { Height = b.Height / 2 + 2 }, Color.FromArgb(160, 160, 165),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, e.Header?.Text ?? "", font, text, Color.FromArgb(222, 222, 222),
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
