using System.IO;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// Persisted strip layout: horizontal offset from the clock anchor (set by dragging)
/// and the strip's size. Offset is physical pixels; size is logical (96-DPI) units so
/// it stays consistent across monitors with different scaling.
/// </summary>
internal sealed class StripLayout
{
    public int Gap { get; set; }
    public int Width { get; set; } = 150;
    public int Height { get; set; } = 40;

    public const int MinWidth = 80, MaxWidth = 400;
    public const int MinHeight = 20, MaxHeight = 80;

    public void Clamp()
    {
        Gap = Math.Max(0, Gap);
        Width = Math.Clamp(Width, MinWidth, MaxWidth);
        Height = Math.Clamp(Height, MinHeight, MaxHeight);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tallybar", "layout.json");

    public static StripLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                StripLayout? layout = JsonSerializer.Deserialize<StripLayout>(File.ReadAllText(FilePath));
                if (layout is not null)
                {
                    layout.Clamp();
                    return layout;
                }
            }
        }
        catch { /* corrupt/locked file: fall back to defaults */ }
        return new StripLayout();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* best effort — layout just won't persist */ }
    }
}
