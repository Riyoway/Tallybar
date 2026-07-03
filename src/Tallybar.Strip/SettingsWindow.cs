using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tallybar;

/// <summary>
/// Settings dialog. Every control writes through to Settings and saves immediately,
/// so the strip live-previews each change. One instance at a time.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static SettingsWindow? _open;

    private readonly Settings _s;
    private readonly TableLayoutPanel _grid;
    private bool _loading = true;

    public static void Open(Settings settings)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow(settings);
        _open.Show();
    }

    private SettingsWindow(Settings settings)
    {
        _s = settings;
        bool light = StripWindow.IsLightTheme(settings);
        Color back = light ? Color.FromArgb(243, 244, 248) : Color.FromArgb(20, 22, 30);
        Color ink = light ? Color.FromArgb(25, 28, 36) : Color.FromArgb(233, 236, 244);

        Text = "Tallybar settings";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = back;
        ForeColor = ink;
        Font = new Font("Segoe UI", 9.5f);

        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 12, 18, 16),
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        Controls.Add(_grid);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildControls();
        _loading = false;

        int dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void BuildControls()
    {
        Header("Providers");
        Check("Claude", _s.ClaudeEnabled, v => _s.ClaudeEnabled = v);
        Check("Codex", _s.CodexEnabled, v => _s.CodexEnabled = v);
        Combo("Strip shows", ["cycle", "claude", "codex"], _s.StripProvider,
            v => _s.StripProvider = v, ["Cycle through providers", "Claude only", "Codex only"]);
        Number("Cycle every (s)", 3, 120, _s.CycleSeconds, v => _s.CycleSeconds = v);

        Header("Strip content");
        Check("Sparkline", _s.ShowSparkline, v => _s.ShowSparkline = v);
        Check("Provider name", _s.ShowLabel, v => _s.ShowLabel = v);
        Check("Percent", _s.ShowPercent, v => _s.ShowPercent = v);
        Check("Countdown / weekly line", _s.ShowSubline, v => _s.ShowSubline = v);

        Header("Appearance");
        Combo("Theme", ["auto", "dark", "light"], _s.Theme,
            v => _s.Theme = v, ["Follow Windows", "Dark", "Light"]);
        Number("Corner radius", 0, 20, _s.CornerRadius, v => _s.CornerRadius = v);
        Number("Opacity (%)", 30, 100, (int)(_s.Opacity * 100), v => _s.Opacity = v / 100.0);
        ColorRow("Healthy color", () => _s.Ok, c => _s.OkColor = ColorTranslator.ToHtml(c));
        ColorRow("Warning color", () => _s.Warn, c => _s.WarnColor = ColorTranslator.ToHtml(c));
        ColorRow("Critical color", () => _s.Crit, c => _s.CritColor = ColorTranslator.ToHtml(c));
        Number("Warn at (%)", 10, 99, (int)(_s.WarnAt * 100), v => _s.WarnAt = v / 100.0);
        Number("Critical at (%)", 10, 100, (int)(_s.CritAt * 100), v => _s.CritAt = v / 100.0);
        Check("Animations", _s.Animations, v => _s.Animations = v);

        Header("Behavior");
        Number("Poll every (s)", 30, 900, _s.PollSeconds, v => _s.PollSeconds = v);
        Check("Strips on secondary taskbars", _s.SecondaryTaskbars, v => _s.SecondaryTaskbars = v);
        Check("Launch at login", IsLaunchAtLogin(), SetLaunchAtLogin, save: false);
    }

    // --- control builders (write-through + immediate save) ---

    private void Changed()
    {
        if (!_loading) _s.Save();
    }

    private void Header(string text)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 6),
        };
        _grid.SetColumnSpan(label, 2);
        _grid.Controls.Add(label);
        _grid.SetColumn(label, 0);
    }

    private void Check(string text, bool value, Action<bool> apply, bool save = true)
    {
        var cb = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(4, 3, 0, 3) };
        cb.CheckedChanged += (_, _) => { apply(cb.Checked); if (save) Changed(); };
        _grid.SetColumnSpan(cb, 2);
        _grid.Controls.Add(cb);
    }

    private void Number(string caption, int min, int max, int value, Action<int> apply)
    {
        AddCaption(caption);
        var num = new NumericUpDown
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            Width = 90, Margin = new Padding(0, 3, 0, 3),
        };
        num.ValueChanged += (_, _) => { apply((int)num.Value); Changed(); };
        _grid.Controls.Add(num);
    }

    private void Combo(string caption, string[] values, string current, Action<string> apply, string[] display)
    {
        AddCaption(caption);
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200, Margin = new Padding(0, 3, 0, 3),
        };
        combo.Items.AddRange(display);
        int idx = Array.IndexOf(values, current);
        combo.SelectedIndex = idx >= 0 ? idx : 0;
        combo.SelectedIndexChanged += (_, _) => { apply(values[combo.SelectedIndex]); Changed(); };
        _grid.Controls.Add(combo);
    }

    private void ColorRow(string caption, Func<Color> get, Action<Color> apply)
    {
        AddCaption(caption);
        var btn = new Button
        {
            Width = 90, Height = 26, Margin = new Padding(0, 3, 0, 3),
            FlatStyle = FlatStyle.Flat, BackColor = get(), Text = "",
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 110);
        btn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = get(), FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                apply(dlg.Color);
                btn.BackColor = dlg.Color;
                Changed();
            }
        };
        _grid.Controls.Add(btn);
    }

    private void AddCaption(string caption)
        => _grid.Controls.Add(new Label
        {
            Text = caption, AutoSize = true, Margin = new Padding(4, 7, 0, 3),
        });

    // --- launch at login (registry is the source of truth; not stored in settings.json) ---

    private static bool IsLaunchAtLogin()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Tallybar") is not null;
        }
        catch { return false; }
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key?.SetValue("Tallybar", $"\"{Application.ExecutablePath}\"");
            else
                key?.DeleteValue("Tallybar", throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_open == this) _open = null;
        base.OnFormClosed(e);
    }
}
