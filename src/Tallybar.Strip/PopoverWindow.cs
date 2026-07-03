using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tallybar;

/// <summary>
/// The click-to-open flyout: per-provider usage bars with reset countdowns on an
/// acrylic (blur-behind) card. Closes when it loses focus. One instance at a time.
/// </summary>
internal sealed class PopoverWindow : Form
{
    private static PopoverWindow? _open;

    private readonly Settings _settings;
    private readonly Poller _poller;
    private readonly Rectangle _anchor; // screen rect of the strip
    private Rectangle _refreshRect;
    private Rectangle _settingsRect;
    private bool _acrylic;

    public static void Toggle(Settings settings, Poller poller, Rectangle anchor)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Close();
            return;
        }
        _open = new PopoverWindow(settings, poller, anchor);
        _open.Show();
        _open.Activate();
    }

    private PopoverWindow(Settings settings, Poller poller, Rectangle anchor)
    {
        _settings = settings;
        _poller = poller;
        _anchor = anchor;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Tallybar";
        DoubleBuffered = true;
        BackColor = StripWindow.IsLightTheme(settings) ? Color.White : Color.Black;

        _poller.Updated += OnPollerUpdated;
    }

    private void OnPollerUpdated()
    {
        if (!IsDisposed) Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool light = StripWindow.IsLightTheme(_settings);

        // Acrylic blur-behind with a theme tint (AABBGGRR); falls back to opaque paint.
        _acrylic = Native.TryEnableAcrylic(Handle, light ? 0xD9F7F5F2 : 0xD9201812);

        int dark = light ? 0 : 1;
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

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float s = DeviceDpi / 96f;

        int rows = 0, providers = 0;
        foreach (IProvider p in EnabledProviders())
        {
            providers++;
            rows += Math.Max(1, _poller.Latest(p.Id).Count);
        }
        if (providers == 0) { providers = 1; rows = 1; }

        int width = (int)(330 * s);
        int height = (int)((16 + providers * 30 + rows * 40 + 40) * s);

        // Above the strip, right-aligned to it, clamped to the working area.
        Rectangle wa = Screen.FromRectangle(_anchor).WorkingArea;
        int x = Math.Max(wa.Left + 8, Math.Min(_anchor.Right - width, wa.Right - width - 8));
        int y = Math.Max(wa.Top + 8, _anchor.Top - height - (int)(10 * s));
        SetBounds(x, y, width, height);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poller.Updated -= OnPollerUpdated;
        if (_open == this) _open = null;
        base.OnFormClosed(e);
    }

    private List<IProvider> EnabledProviders() =>
        [.. _poller.Providers.Where(p => _settings.IsProviderEnabled(p.Id))];

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float s = DeviceDpi / 96f;
        bool light = StripWindow.IsLightTheme(_settings);

        Color ink = light ? Color.FromArgb(20, 24, 32) : Color.FromArgb(233, 236, 244);
        Color mut = light ? Color.FromArgb(110, 116, 128) : Color.FromArgb(154, 161, 178);
        Color faint = Color.FromArgb(light ? 30 : 24, light ? Color.Black : Color.White);

        // With blur-behind acrylic, GDI pixels are composited over the blur — the base
        // coat must match the theme (black shows the dark tint through; anything else,
        // like the default Control color, washes the whole card out white).
        if (_acrylic)
            g.Clear(light ? Color.White : Color.Black);
        else // opaque fallback (no blur available)
            g.Clear(light ? Color.FromArgb(242, 245, 247) : Color.FromArgb(18, 21, 31));

        using var fTitle = new Font("Segoe UI", 10.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fName = new Font("Segoe UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fRow = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fSmall = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var inkB = new SolidBrush(ink);
        using var mutB = new SolidBrush(mut);

        float pad = 14 * s;
        float y = pad;
        float w = Width - pad * 2;

        foreach (IProvider p in EnabledProviders())
        {
            var snaps = _poller.Latest(p.Id);
            UsageSnapshot? first = snaps.FirstOrDefault();

            g.DrawString(p.DisplayName, fName, inkB, pad, y);
            string state = first?.Status switch
            {
                FetchStatus.AuthError => "sign-in needed",
                FetchStatus.NotConfigured => "not configured",
                FetchStatus.Offline => "offline",
                FetchStatus.Stale => "stale",
                _ => "",
            };
            if (state.Length > 0)
            {
                SizeF sz = g.MeasureString(state, fSmall);
                g.DrawString(state, fSmall, mutB, Width - pad - sz.Width, y + 2 * s);
            }
            y += 26 * s;

            if (snaps.Count == 0 || first is null || double.IsNaN(first.Fraction))
            {
                g.DrawString("no data yet", fSmall, mutB, pad, y);
                y += 40 * s;
            }
            else
            {
                foreach (UsageSnapshot snap in snaps)
                {
                    if (double.IsNaN(snap.Fraction)) continue;
                    Color tone = _settings.ColorFor(snap.Fraction);

                    g.DrawString(snap.WindowLabel, fRow, mutB, pad, y);
                    string pct = $"{(int)Math.Round(snap.Fraction * 100)}%";
                    SizeF pctSz = g.MeasureString(pct, fRow);
                    using (var toneB = new SolidBrush(tone))
                        g.DrawString(pct, fRow, toneB, Width - pad - pctSz.Width, y);
                    y += 19 * s;

                    var barRect = new RectangleF(pad, y, w, 5 * s);
                    using (var back = new SolidBrush(faint))
                        FillRounded(g, back, barRect, 2.5f * s);
                    using (var fore = new SolidBrush(tone))
                        FillRounded(g, fore,
                            new RectangleF(pad, y, w * (float)Math.Clamp(snap.Fraction, 0, 1), 5 * s), 2.5f * s);
                    y += 9 * s;

                    if (snap.ResetsAt is { } r)
                        g.DrawString("resets in " + FormatCountdown(r), fSmall, mutB, pad, y);
                    y += 12 * s;
                }
                y += 8 * s;
            }
        }

        // Footer: age + actions.
        using (var pen = new Pen(faint))
            g.DrawLine(pen, pad, Height - 34 * s, Width - pad, Height - 34 * s);

        DateTimeOffset? newest = EnabledProviders()
            .SelectMany(p => _poller.Latest(p.Id))
            .Select(sn => (DateTimeOffset?)sn.FetchedAt)
            .DefaultIfEmpty(null)
            .Max();
        string age = newest is { } t ? $"updated {Math.Max(0, (int)(DateTimeOffset.UtcNow - t).TotalSeconds)}s ago" : "";
        g.DrawString(age, fSmall, mutB, pad, Height - 27 * s);

        // Footer actions: system icon-font glyph + label.
        float glyphPx = 12 * s, gap = 5 * s;
        using Font fIcon = Icons.GetFont(glyphPx);
        using var accent = new SolidBrush(_settings.Ok);

        float setW = g.MeasureString(Icons.Settings, fIcon).Width - 4 + gap
                   + g.MeasureString("Settings", fTitle).Width;
        float refW = g.MeasureString(Icons.Refresh, fIcon).Width - 4 + gap
                   + g.MeasureString("Refresh", fTitle).Width;
        int actionY = (int)(Height - 28 * s);
        _settingsRect = new Rectangle((int)(Width - pad - setW), actionY, (int)setW + 4, (int)(16 * s));
        _refreshRect = new Rectangle((int)(_settingsRect.Left - refW - 16 * s), actionY, (int)refW + 4, (int)(16 * s));

        float x1 = _refreshRect.Left + Icons.Draw(g, Icons.Refresh, glyphPx, accent, _refreshRect.Left, actionY + 1 * s);
        g.DrawString("Refresh", fTitle, accent, x1 + gap, actionY);
        float x2 = _settingsRect.Left + Icons.Draw(g, Icons.Settings, glyphPx, accent, _settingsRect.Left, actionY + 1 * s);
        g.DrawString("Settings", fTitle, accent, x2 + gap, actionY);
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static string FormatCountdown(DateTimeOffset resetsAt)
    {
        TimeSpan t = resetsAt - DateTimeOffset.UtcNow;
        if (t <= TimeSpan.Zero) return "moments";
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{t.Minutes}m";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _refreshRect.Contains(e.Location) || _settingsRect.Contains(e.Location)
            ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_refreshRect.Contains(e.Location))
        {
            _poller.RefreshNow();
            Invalidate();
        }
        else if (_settingsRect.Contains(e.Location))
        {
            Close();
            SettingsWindow.Open(_settings);
        }
    }
}
