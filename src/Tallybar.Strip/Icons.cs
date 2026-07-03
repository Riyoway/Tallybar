using System.Drawing;

namespace Tallybar;

/// <summary>
/// System icon-font glyphs (Segoe Fluent Icons on Win11, Segoe MDL2 Assets on Win10).
/// Stroke-style, pixel-crisp at any DPI, and consistent with the OS — no icon assets
/// to ship or rasterize.
/// </summary>
internal static class Icons
{
    public const string Refresh = "\uE72C";
    public const string Settings = "\uE713";
    public const string Clock = "\uE823";   // "Recent" — outline clock
    public const string Close = "";   // ChromeClose
    public const string Add = "";      // plus
    public const string Remove = "";   // minus

    private static string? _family;

    public static Font GetFont(float px)
    {
        if (_family is null)
        {
            using var probe = new Font("Segoe Fluent Icons", 10f);
            _family = probe.Name == "Segoe Fluent Icons" ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }
        return new Font(_family, px, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>Draw a glyph and return its advance width.</summary>
    public static float Draw(Graphics g, string glyph, float px, Brush brush, float x, float y)
    {
        using Font font = GetFont(px);
        g.DrawString(glyph, font, brush, x, y);
        return g.MeasureString(glyph, font).Width - 4; // trim GDI+ padding
    }
}
