using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Tallybar;

/// <summary>
/// The click-to-open flyout, styled like a native menu-bar popover: a provider tab bar
/// (monogram tile + name + mini usage bar), the selected provider's windows as titled
/// progress sections ("8% used" / "Resets in 3h 41m"), and a menu footer
/// (Refresh · Settings… · About · Quit). Acrylic card, closes on focus loss.
/// </summary>
internal sealed class PopoverWindow : Form
{
    private static PopoverWindow? _open;
    private static string? _selectedId; // persists across opens

    private readonly Settings _settings;
    private readonly Poller _poller;
    private readonly Rectangle _anchor;
    private readonly float _sc;
    private readonly bool _light;
    private readonly Color _ink, _mut, _faint, _base;

    private readonly List<(RectangleF Rect, string Id)> _tabs = [];
    private readonly List<(RectangleF Rect, string Id)> _menu = [];
    private string _hover = "";

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
        _sc = DeviceDpi / 96f;
        _light = StripWindow.IsLightTheme(settings);

        _base = _light ? Color.White : Color.Black;
        _ink = _light ? Color.FromArgb(24, 28, 36) : Color.FromArgb(236, 239, 246);
        _mut = _light ? Color.FromArgb(110, 116, 128) : Color.FromArgb(152, 159, 176);
        _faint = Color.FromArgb(_light ? 26 : 30, _light ? Color.Black : Color.White);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Tallybar";
        DoubleBuffered = true;
        BackColor = _base;

        _poller.Updated += OnPollerUpdated;
        Place();
    }

    // --- data ---

    private List<IProvider> Providers() =>
        [.. _poller.Providers.Where(p => _settings.IsProviderEnabled(p.Id))];

    private IProvider? Selected()
    {
        var list = Providers();
        return list.FirstOrDefault(p => p.Id == _selectedId) ?? list.FirstOrDefault();
    }

    private void OnPollerUpdated()
    {
        if (!IsDisposed) RenderSurface();
    }

    // --- geometry ---

    private float Pad => 16 * _sc;
    private float TabsH => 64 * _sc;
    private float MenuRowH => 30 * _sc;

    private float DetailHeight(IProvider? p)
    {
        if (p is null) return 60 * _sc;
        var snaps = _poller.Latest(p.Id).Where(s => !double.IsNaN(s.Fraction)).ToList();
        float head = 52 * _sc;
        float windows = snaps.Count == 0 ? 30 * _sc : snaps.Count * 62 * _sc;
        return head + windows;
    }

    private void Place()
    {
        int width = (int)(330 * _sc);
        float menuH = MenuRowH * 4 + 14 * _sc;
        int height = (int)(TabsH + DetailHeight(Selected()) + menuH + 10 * _sc);

        Rectangle wa = Screen.FromRectangle(_anchor).WorkingArea;
        height = Math.Min(height, wa.Height - 16);
        int x = Math.Max(wa.Left + 8, Math.Min(_anchor.Right - width, wa.Right - width - 8));
        int y = Math.Max(wa.Top + 8, _anchor.Top - height - (int)(10 * _sc));
        SetBounds(x, y, width, height);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED; // per-pixel-alpha translucent surface
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Ask the compositor to blur behind our translucent pixels where supported.
        Native.TryEnableAcrylic(Handle, _light ? 0x40F7F5F2u : 0x40201812u);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RenderSurface();
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

    // --- paint (rendered to a layered ARGB surface for real translucency) ---

    protected override void OnPaintBackground(PaintEventArgs e) { /* layered; nothing to erase */ }

    private void RenderSurface()
    {
        if (!IsHandleCreated || IsDisposed) return;
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            PaintContent(g);
        }
        LayeredSurface.Push(Handle, bmp, Left, Top);
    }

    private void PaintContent(Graphics g)
    {
        // Translucent rounded card so the desktop (and any compositor blur) shows through.
        var card = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(Color.FromArgb(_light ? 234 : 216, _base)))
            FillRounded(g, bg, card, 12 * _sc);
        using (var edge = new Pen(Color.FromArgb(_light ? 42 : 48, _light ? Color.Black : Color.White)))
            StrokeRounded(g, edge, card, 12 * _sc);

        _tabs.Clear();
        _menu.Clear();
        float y = PaintTabs(g);
        y = PaintDetail(g, y);
        PaintMenu(g, y);
    }

    private float PaintTabs(Graphics g)
    {
        var providers = Providers();
        if (providers.Count == 0) return TabsH;

        float tabW = Math.Min(72 * _sc, (Width - Pad * 2) / providers.Count);
        float x = Pad;
        float top = 10 * _sc;
        IProvider? selected = Selected();

        using var fName = new Font("Segoe UI", 10.5f * _sc, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

        foreach (IProvider p in providers)
        {
            var tabRect = new RectangleF(x, top, tabW, TabsH - 16 * _sc);
            _tabs.Add((tabRect, p.Id));
            bool isSel = p.Id == selected?.Id;

            // Selected = a soft accent-tinted card with an accent outline (not a heavy
            // solid fill); hover = a faint neutral tint. The logo keeps its brand colour.
            if (isSel)
            {
                using (var selBg = new SolidBrush(Color.FromArgb(_light ? 30 : 42, Accent)))
                    FillRounded(g, selBg, tabRect, 9 * _sc);
                using (var selEdge = new Pen(Color.FromArgb(_light ? 150 : 120, Accent)))
                    StrokeRounded(g, selEdge, tabRect, 9 * _sc);
            }
            else if (_hover == "tab:" + p.Id)
            {
                using var hovBg = new SolidBrush(_faint);
                FillRounded(g, hovBg, tabRect, 9 * _sc);
            }

            // Real brand logo; monogram chip if absent.
            (string mono, Color brand) = Brand(p.Id);
            float tile = 22 * _sc;
            var tileRect = new RectangleF(x + (tabW - tile) / 2, top + 4 * _sc, tile, tile);

            if (!Logos.Draw(g, p.Id, tileRect, LogoColor(p.Id, brand)))
            {
                using (var tileBg = new SolidBrush(brand))
                    FillRounded(g, tileBg, tileRect, 6 * _sc);
                using var fTile = new Font("Segoe UI", 9.5f * _sc, FontStyle.Bold, GraphicsUnit.Pixel);
                using var tb = new SolidBrush(TextOn(brand));
                var tfmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(mono, fTile, tb, tileRect, tfmt);
            }

            // Name — accent-coloured when selected, so the tab reads as active.
            using (var nb = new SolidBrush(isSel ? Accent : _ink))
                g.DrawString(p.DisplayName, fName, nb,
                    new RectangleF(x, top + tile + 8 * _sc, tabW, fName.Height), fmt);

            // Mini usage bar.
            UsageSnapshot? s = _poller.Latest(p.Id).FirstOrDefault();
            var barRect = new RectangleF(x + tabW * 0.2f, tabRect.Bottom + 4 * _sc, tabW * 0.6f, 3.5f * _sc);
            using (var barBg = new SolidBrush(_faint))
                FillRounded(g, barBg, barRect, barRect.Height / 2);
            if (s is not null && !double.IsNaN(s.Fraction))
            {
                using var barFg = new SolidBrush(State(s.Fraction));
                FillRounded(g, barFg,
                    barRect with { Width = Math.Max(barRect.Height, barRect.Width * (float)Math.Clamp(s.Fraction, 0, 1)) },
                    barRect.Height / 2);
            }

            x += tabW;
        }
        return TabsH + 4 * _sc;
    }

    private float PaintDetail(Graphics g, float y)
    {
        IProvider? p = Selected();
        using var fBig = new Font("Segoe UI", 16f * _sc, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fTitle = new Font("Segoe UI", 12.5f * _sc, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fSmall = new Font("Segoe UI", 11f * _sc, FontStyle.Regular, GraphicsUnit.Pixel);
        using var inkB = new SolidBrush(_ink);
        using var mutB = new SolidBrush(_mut);

        if (p is null)
        {
            g.DrawString("No providers enabled", fSmall, mutB, Pad, y + 10 * _sc);
            return y + 60 * _sc;
        }

        var all = _poller.Latest(p.Id);
        var snaps = all.Where(s => !double.IsNaN(s.Fraction)).ToList();

        // Header: name + updated-ago / status.
        g.DrawString(p.DisplayName, fBig, inkB, Pad, y + 2 * _sc);
        DateTimeOffset? fetched = all.Select(s => (DateTimeOffset?)s.FetchedAt).DefaultIfEmpty(null).Max();
        string age = fetched is { } t ? $"Updated {Age(t)}" : "Waiting for data…";
        g.DrawString(age, fSmall, mutB, Pad, y + 24 * _sc);
        string state = all.FirstOrDefault()?.Status switch
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
            g.DrawString(state, fSmall, mutB, Width - Pad - sz.Width, y + 24 * _sc);
        }
        y += 46 * _sc;
        using (var pen = new Pen(_faint)) g.DrawLine(pen, Pad, y, Width - Pad, y);
        y += 6 * _sc;

        if (snaps.Count == 0)
        {
            g.DrawString("no data yet", fSmall, mutB, Pad, y + 4 * _sc);
            return y + 30 * _sc;
        }

        foreach (UsageSnapshot s in snaps)
        {
            Color tone = State(s.Fraction);

            g.DrawString(TitleCase(s.WindowLabel), fTitle, inkB, Pad, y);
            y += 20 * _sc;

            var barRect = new RectangleF(Pad, y, Width - Pad * 2, 6 * _sc);
            using (var barBg = new SolidBrush(_faint))
                FillRounded(g, barBg, barRect, 3 * _sc);
            using (var barFg = new SolidBrush(tone))
                FillRounded(g, barFg,
                    barRect with { Width = Math.Max(barRect.Height, barRect.Width * (float)Math.Clamp(s.Fraction, 0, 1)) },
                    3 * _sc);
            y += 11 * _sc;

            g.DrawString($"{(int)Math.Round(s.Fraction * 100)}% used", fSmall, mutB, Pad, y);
            if (s.ResetsAt is { } r)
            {
                string reset = "Resets in " + Countdown(r);
                SizeF sz = g.MeasureString(reset, fSmall);
                g.DrawString(reset, fSmall, mutB, Width - Pad - sz.Width, y);
            }
            y += 31 * _sc;
        }
        return y;
    }

    private void PaintMenu(Graphics g, float y)
    {
        using (var pen = new Pen(_faint)) g.DrawLine(pen, Pad, y, Width - Pad, y);
        y += 6 * _sc;

        using var fRow = new Font("Segoe UI", 12f * _sc, FontStyle.Regular, GraphicsUnit.Pixel);

        void Row(string id, string text, string glyph = "", string logoId = "")
        {
            var rect = new RectangleF(6 * _sc, y, Width - 12 * _sc, MenuRowH);
            _menu.Add((rect, id));
            if (_hover == id)
            {
                using var hov = new SolidBrush(_faint);
                FillRounded(g, hov, rect, 6 * _sc);
            }
            float tx = Pad;
            using var b = new SolidBrush(_ink);
            if (logoId.Length > 0)
            {
                float sz = 15 * _sc;
                Logos.Draw(g, logoId, new RectangleF(tx, y + (MenuRowH - sz) / 2, sz, sz), _ink);
                tx += sz + 8 * _sc;
            }
            else if (glyph.Length > 0)
            {
                tx += Icons.Draw(g, glyph, 12 * _sc, b, tx, y + (MenuRowH - 14 * _sc) / 2) + 8 * _sc;
            }
            g.DrawString(text, fRow, b, tx, y + (MenuRowH - fRow.Height) / 2);
            y += MenuRowH;
        }

        Row("refresh", "Refresh", Icons.Refresh);
        Row("settings", "Settings", Icons.Settings);
        Row("about", "About Tallybar", logoId: "github");
        Row("quit", "Quit");
    }

    // --- input ---

    private string HitTest(Point pt)
    {
        foreach ((RectangleF r, string id) in _tabs)
            if (r.Contains(pt)) return "tab:" + id;
        foreach ((RectangleF r, string id) in _menu)
            if (r.Contains(pt)) return id;
        return "";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            RenderSurface();
        }
        Cursor = hit.Length > 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        string hit = HitTest(e.Location);
        if (hit.StartsWith("tab:", StringComparison.Ordinal))
        {
            _selectedId = hit[4..];
            Place();      // height depends on the selected provider's window count
            RenderSurface();
            return;
        }
        switch (hit)
        {
            case "refresh":
                _poller.RefreshNow();
                RenderSurface();
                break;
            case "settings":
                Close();
                SettingsWindow.Open(_settings, _anchor);
                break;
            case "about":
                try { Process.Start(new ProcessStartInfo("https://github.com/Riyoway/Tallybar") { UseShellExecute = true }); }
                catch { }
                break;
            case "quit":
                Application.Exit();
                break;
        }
    }

    // --- helpers ---

    // State/accent colours, deepened on light themes so they stay legible.
    private Color Accent => Palette.Readable(_settings.Ok, _light);
    private Color State(double fraction) => Palette.Readable(_settings.ColorFor(fraction), _light);

    private static Color TextOn(Color bg) => Palette.TextOn(bg);

    // Colour to tint the logo with on an unselected tab: the brand hue where it reads on
    // both themes, otherwise the theme ink (for near-monochrome marks).
    private Color LogoColor(string id, Color brand) => id switch
    {
        "claude" => brand,
        "gemini" => Color.FromArgb(0x34, 0x86, 0xF6),
        "codex" => Color.FromArgb(0x7C, 0x8A, 0xFF),
        _ => _ink,
    };

    private static (string Monogram, Color Brand) Brand(string id) => id switch
    {
        "claude" => ("Cl", Color.FromArgb(0xD9, 0x77, 0x57)),
        "codex" => ("Cx", Color.FromArgb(0x10, 0xA3, 0x7F)),
        "copilot" => ("Co", Color.FromArgb(0x54, 0x6E, 0x7A)),
        "gemini" => ("Ge", Color.FromArgb(0x42, 0x85, 0xF4)),
        "cursor" => ("Cu", Color.FromArgb(0x6B, 0x72, 0x80)),
        _ => (id.Length > 0 ? char.ToUpperInvariant(id[0]).ToString() : "?", Color.FromArgb(0x80, 0x86, 0x94)),
    };

    private static string TitleCase(string label)
        => label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];

    private static string Age(DateTimeOffset t)
    {
        TimeSpan d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 15) return "just now";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        return $"{(int)d.TotalHours}h ago";
    }

    private static string Countdown(DateTimeOffset resetsAt)
    {
        TimeSpan t = resetsAt - DateTimeOffset.UtcNow;
        if (t <= TimeSpan.Zero) return "moments";
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{t.Minutes}m";
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, radius);
        g.FillPath(brush, path);
    }

    private static void StrokeRounded(Graphics g, Pen pen, RectangleF r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
