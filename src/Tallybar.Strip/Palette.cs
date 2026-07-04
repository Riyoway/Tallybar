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

    /// <summary>
    /// Black or white ink for text/logos on <paramref name="bg"/>, chosen by actual WCAG
    /// contrast rather than HSL lightness (which wrongly picks white on bright greens).
    /// </summary>
    public static Color TextOn(Color bg)
    {
        double l = RelativeLuminance(bg);
        double contrastWhite = 1.05 / (l + 0.05);
        double contrastBlack = (l + 0.05) / 0.05;
        return contrastBlack >= contrastWhite ? Color.FromArgb(18, 22, 28) : Color.White;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R / 255.0) + 0.7152 * Lin(c.G / 255.0) + 0.0722 * Lin(c.B / 255.0);
    }
}
