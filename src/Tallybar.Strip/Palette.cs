using System.Drawing;

namespace Tallybar;

internal static class Palette
{
    /// <summary>
    /// Keep an accent legible on the current surface. The default state colours are tuned
    /// for dark taskbars; on a light theme a light mint/amber washes out, so deepen any
    /// too-light colour (leaving already-dark custom colours alone).
    /// </summary>
    public static Color Readable(Color c, bool light)
    {
        if (!light) return c;
        float b = c.GetBrightness();
        if (b <= 0.5f) return c;
        float f = 0.42f / b; // land around a mid-dark brightness
        return Color.FromArgb(c.A, Ch(c.R * f), Ch(c.G * f), Ch(c.B * f));
    }

    private static int Ch(float v) => Math.Clamp((int)v, 0, 255);
}
