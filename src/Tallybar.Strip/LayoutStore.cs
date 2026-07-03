using System.IO;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// Persists the strip's horizontal position so it survives restarts. Stored as a "gap":
/// physical pixels to shift left from the default clock anchor, set by dragging the strip.
/// </summary>
internal static class LayoutStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tallybar", "layout.json");

    public static int LoadGap()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Layout? layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(FilePath));
                return Math.Max(0, layout?.Gap ?? 0);
            }
        }
        catch { /* corrupt/locked file: fall back to default position */ }
        return 0;
    }

    public static void SaveGap(int gap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Layout { Gap = Math.Max(0, gap) }));
        }
        catch { /* best effort — position just won't persist */ }
    }

    private sealed class Layout
    {
        public int Gap { get; set; }
    }
}
