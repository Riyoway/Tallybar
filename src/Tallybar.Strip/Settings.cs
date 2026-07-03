using System.Drawing;
using System.IO;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// All user-tunable state, persisted to %LOCALAPPDATA%\Tallybar\settings.json.
/// Geometry is stored in logical (96-DPI) units so it stays consistent across
/// monitors; Gap is physical pixels along the anchor taskbar.
/// </summary>
public sealed class Settings
{
    // --- placement / geometry ---
    public int Gap { get; set; }
    public int Width { get; set; } = 150;
    public int Height { get; set; } = 40;
    public int CornerRadius { get; set; } = 9;
    public double Opacity { get; set; } = 1.0;          // 0.3 .. 1.0
    public bool SecondaryTaskbars { get; set; }

    // --- strip content ---
    public bool ShowSparkline { get; set; } = true;
    public bool ShowLabel { get; set; } = true;
    public bool ShowPercent { get; set; } = true;
    public bool ShowSubline { get; set; } = true;       // countdown + weekly line
    public string StripProvider { get; set; } = "cycle"; // provider id, or "cycle"
    public int CycleSeconds { get; set; } = 10;

    // --- appearance ---
    public string Theme { get; set; } = "auto";         // auto | dark | light
    public string OkColor { get; set; } = "#4FD693";
    public string WarnColor { get; set; } = "#FFCF6B";
    public string CritColor { get; set; } = "#FF6F7A";
    public double WarnAt { get; set; } = 0.75;
    public double CritAt { get; set; } = 0.90;
    public bool Animations { get; set; } = true;

    // --- behavior ---
    public int PollSeconds { get; set; } = 60;
    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;

    public const int MinWidth = 80, MaxWidth = 400;
    public const int MinHeight = 20, MaxHeight = 80;

    /// <summary>Raised after Save(); listeners re-read whatever they cache.</summary>
    public event Action? Changed;

    public bool IsProviderEnabled(string id) => id switch
    {
        "claude" => ClaudeEnabled,
        "codex" => CodexEnabled,
        _ => true,
    };

    public Color Ok => Parse(OkColor, Color.FromArgb(79, 214, 147));
    public Color Warn => Parse(WarnColor, Color.FromArgb(255, 207, 107));
    public Color Crit => Parse(CritColor, Color.FromArgb(255, 111, 122));

    public Color ColorFor(double fraction) =>
        fraction >= CritAt ? Crit : fraction >= WarnAt ? Warn : Ok;

    private static Color Parse(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    public void Clamp()
    {
        Gap = Math.Max(0, Gap);
        Width = Math.Clamp(Width, MinWidth, MaxWidth);
        Height = Math.Clamp(Height, MinHeight, MaxHeight);
        CornerRadius = Math.Clamp(CornerRadius, 0, 20);
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        CycleSeconds = Math.Clamp(CycleSeconds, 3, 120);
        PollSeconds = Math.Clamp(PollSeconds, 30, 900);
        WarnAt = Math.Clamp(WarnAt, 0.1, 0.99);
        CritAt = Math.Clamp(CritAt, WarnAt, 1.0);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tallybar", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Settings? s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s is not null)
                {
                    s.Clamp();
                    return s;
                }
            }
        }
        catch { /* corrupt/locked file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
        Changed?.Invoke();
    }
}
