using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tallybar;

/// <summary>
/// Settings as a glassy flyout anchored above the strip — the same visual surface as
/// the usage popover, not a separate framed window. Borderless, acrylic, rounded, dark.
/// Every control writes through to Settings and saves immediately, so the strip
/// live-previews each change. One instance at a time; closes on Esc or the × button.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static SettingsWindow? _open;

    private readonly Settings _s;
    private readonly Rectangle _anchor;
    private readonly bool _light;
    private readonly Color _ink, _mut, _input, _base;
    private readonly float _scale;
    private TableLayoutPanel _grid = null!;
    private bool _loading = true;

    public static void Open(Settings settings, Rectangle anchor)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow(settings, anchor);
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow(Settings settings, Rectangle anchor)
    {
        _s = settings;
        _anchor = anchor;
        _light = StripWindow.IsLightTheme(settings);
        _scale = DeviceDpi / 96f;

        _base = _light ? Color.White : Color.Black;
        _ink = _light ? Color.FromArgb(24, 28, 36) : Color.FromArgb(233, 236, 244);
        _mut = _light ? Color.FromArgb(110, 116, 128) : Color.FromArgb(154, 161, 178);
        _input = _light ? Color.FromArgb(245, 246, 248) : Color.FromArgb(40, 44, 56);

        Text = "Tallybar settings";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        BackColor = _base;
        ForeColor = _ink;
        Font = new Font("Segoe UI", 9f * _scale);

        BuildLayout();
        _loading = false;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // keep off Alt-Tab
            return cp;
        }
    }

    private void BuildLayout()
    {
        float s = _scale;

        // Header: title + close glyph.
        var header = new Panel { Dock = DockStyle.Top, Height = (int)(42 * s), BackColor = _base };
        var title = new Label
        {
            Text = "Settings",
            Font = new Font("Segoe UI", 12f * s, FontStyle.Bold),
            ForeColor = _ink,
            AutoSize = true,
            Location = new Point((int)(16 * s), (int)(11 * s)),
            BackColor = Color.Transparent,
        };
        var close = new Label
        {
            Text = Icons.Close,
            Font = Icons.GetFont(11 * s),
            ForeColor = _mut,
            AutoSize = false,
            Size = new Size((int)(30 * s), (int)(30 * s)),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
        };
        close.Click += (_, _) => Close();
        close.MouseEnter += (_, _) => close.ForeColor = _ink;
        close.MouseLeave += (_, _) => close.ForeColor = _mut;
        header.Controls.Add(title);
        header.Controls.Add(close);
        header.Resize += (_, _) => close.Location = new Point(header.Width - close.Width - (int)(6 * s), (int)(6 * s));

        // Scrollable content.
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = _base };
        _grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding((int)(16 * s), (int)(4 * s), (int)(16 * s), (int)(14 * s)),
            BackColor = _base,
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170 * s));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168 * s));
        content.Controls.Add(_grid);

        Controls.Add(content);
        Controls.Add(header);

        BuildControls();

        // Size: fixed width, height fits content but clamped to the working area.
        int width = (int)(354 * s);
        int desired = header.Height + _grid.PreferredSize.Height + (int)(10 * s);
        Rectangle wa = Screen.FromRectangle(_anchor).WorkingArea;
        int height = Math.Min(desired, wa.Height - (int)(16 * s));

        // Anchored above the strip, right-aligned, clamped to the working area.
        int x = Math.Max(wa.Left + 8, Math.Min(_anchor.Right - width, wa.Right - width - 8));
        int y = Math.Max(wa.Top + 8, _anchor.Top - height - (int)(10 * s));
        SetBounds(x, y, width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Same glass treatment as the usage popover.
        Native.TryEnableAcrylic(Handle, _light ? 0xD9F7F5F2 : 0xD9201812);
        int dark = _light ? 0 : 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        else
        {
            Native.SetWindowRgn(Handle,
                Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12), true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
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

    // --- control builders (dark-themed, write-through + immediate save) ---

    private void Changed()
    {
        if (!_loading) _s.Save();
    }

    private void Header(string text)
    {
        var label = new Label
        {
            Text = text.ToUpperInvariant(),
            Font = new Font("Segoe UI", 8f * _scale, FontStyle.Bold),
            ForeColor = _mut,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, (int)(14 * _scale), 0, (int)(6 * _scale)),
        };
        _grid.SetColumnSpan(label, 2);
        _grid.Controls.Add(label);
        _grid.SetColumn(label, 0);
    }

    private void Check(string text, bool value, Action<bool> apply, bool save = true)
    {
        var cb = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            ForeColor = _ink,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding((int)(2 * _scale), (int)(3 * _scale), 0, (int)(3 * _scale)),
        };
        cb.FlatAppearance.BorderColor = _mut;
        cb.CheckedChanged += (_, _) => { apply(cb.Checked); if (save) Changed(); };
        _grid.SetColumnSpan(cb, 2);
        _grid.Controls.Add(cb);
    }

    private void Number(string caption, int min, int max, int value, Action<int> apply)
    {
        AddCaption(caption);
        var num = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = (int)(84 * _scale),
            BackColor = _input,
            ForeColor = _ink,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, (int)(3 * _scale), 0, (int)(3 * _scale)),
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
            FlatStyle = FlatStyle.Flat,
            Width = (int)(162 * _scale),
            BackColor = _input,
            ForeColor = _ink,
            Margin = new Padding(0, (int)(3 * _scale), 0, (int)(3 * _scale)),
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
            Width = (int)(84 * _scale),
            Height = (int)(24 * _scale),
            FlatStyle = FlatStyle.Flat,
            BackColor = get(),
            Text = "",
            Margin = new Padding(0, (int)(3 * _scale), 0, (int)(3 * _scale)),
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
            Text = caption,
            AutoSize = true,
            ForeColor = _ink,
            BackColor = Color.Transparent,
            Margin = new Padding((int)(2 * _scale), (int)(7 * _scale), 0, (int)(3 * _scale)),
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
