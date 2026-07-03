using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tallybar;

/// <summary>
/// Settings rendered on the same custom-drawn acrylic surface as the usage popover:
/// no framed window, no stock widgets — hand-painted toggle switches, steppers,
/// segmented selectors, and colour swatches on a rounded dark-glass card anchored
/// above the strip. Every change writes through to Settings and live-previews.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static SettingsWindow? _open;

    private readonly Settings _s;
    private readonly Rectangle _anchor;
    private readonly float _sc;
    private readonly bool _light;
    private readonly List<Row> _rows = [];

    internal Color Ink, Mut, Faint, Base;
    internal Color Accent => _s.Ok; // live, so a colour change recolours controls on repaint
    private RectangleF _closeRect;
    private bool _closeHot;
    private float _scroll;
    private float _contentHeight;
    private float HeaderH => 44 * _sc;
    private float Pad => 14 * _sc;

    public static void Open(Settings settings, Rectangle anchor)
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new SettingsWindow(settings, anchor);
        _open.Show();
        _open.Activate();
    }


    private SettingsWindow(Settings settings, Rectangle anchor)
    {
        _s = settings;
        _anchor = anchor;
        _sc = DeviceDpi / 96f;
        _light = StripWindow.IsLightTheme(settings);

        Base = _light ? Color.White : Color.Black;
        Ink = _light ? Color.FromArgb(24, 28, 36) : Color.FromArgb(233, 236, 244);
        Mut = _light ? Color.FromArgb(110, 116, 128) : Color.FromArgb(154, 161, 178);
        Faint = Color.FromArgb(_light ? 26 : 28, _light ? Color.Black : Color.White);

        Text = "Tallybar settings";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Base;

        BuildRows();
        Layout_();
        Place();
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
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

    // --- model ---

    private void BuildRows()
    {
        _rows.Add(new Section("Providers"));
        _rows.Add(new Toggle("Claude", () => _s.ClaudeEnabled, v => _s.ClaudeEnabled = v));
        _rows.Add(new Toggle("Codex", () => _s.CodexEnabled, v => _s.CodexEnabled = v));
        _rows.Add(new Segment("Strip shows", ["Cycle", "Claude", "Codex"],
            () => Array.IndexOf(new[] { "cycle", "claude", "codex" }, _s.StripProvider) is var i && i >= 0 ? i : 0,
            i => _s.StripProvider = new[] { "cycle", "claude", "codex" }[i]));
        _rows.Add(new Stepper("Cycle every", 3, 120, 1, () => _s.CycleSeconds, v => _s.CycleSeconds = v, v => $"{v}s"));

        _rows.Add(new Section("Strip content"));
        _rows.Add(new Toggle("Sparkline", () => _s.ShowSparkline, v => _s.ShowSparkline = v));
        _rows.Add(new Toggle("Provider name", () => _s.ShowLabel, v => _s.ShowLabel = v));
        _rows.Add(new Toggle("Percent", () => _s.ShowPercent, v => _s.ShowPercent = v));
        _rows.Add(new Toggle("Countdown / weekly line", () => _s.ShowSubline, v => _s.ShowSubline = v));

        _rows.Add(new Section("Appearance"));
        _rows.Add(new Segment("Theme", ["Auto", "Dark", "Light"],
            () => Array.IndexOf(new[] { "auto", "dark", "light" }, _s.Theme) is var i && i >= 0 ? i : 0,
            i => _s.Theme = new[] { "auto", "dark", "light" }[i]));
        _rows.Add(new Stepper("Corner radius", 0, 20, 1, () => _s.CornerRadius, v => _s.CornerRadius = v, v => $"{v}"));
        _rows.Add(new Stepper("Opacity", 30, 100, 5, () => (int)(_s.Opacity * 100), v => _s.Opacity = v / 100.0, v => $"{v}%"));
        _rows.Add(new ColorSwatch("Healthy", () => _s.Ok, c => _s.OkColor = ColorTranslator.ToHtml(c)));
        _rows.Add(new ColorSwatch("Warning", () => _s.Warn, c => _s.WarnColor = ColorTranslator.ToHtml(c)));
        _rows.Add(new ColorSwatch("Critical", () => _s.Crit, c => _s.CritColor = ColorTranslator.ToHtml(c)));
        _rows.Add(new Stepper("Warn at", 10, 99, 5, () => (int)(_s.WarnAt * 100), v => _s.WarnAt = v / 100.0, v => $"{v}%"));
        _rows.Add(new Stepper("Critical at", 10, 100, 5, () => (int)(_s.CritAt * 100), v => _s.CritAt = v / 100.0, v => $"{v}%"));
        _rows.Add(new Toggle("Animations", () => _s.Animations, v => _s.Animations = v));

        _rows.Add(new Section("Behavior"));
        _rows.Add(new Stepper("Poll every", 30, 900, 30, () => _s.PollSeconds, v => _s.PollSeconds = v, v => $"{v}s"));
        _rows.Add(new Toggle("Secondary taskbars", () => _s.SecondaryTaskbars, v => _s.SecondaryTaskbars = v));
        _rows.Add(new Toggle("Launch at login", IsLaunchAtLogin, SetLaunchAtLogin, save: false));
    }

    private void Layout_()
    {
        float y = 0;
        foreach (Row r in _rows) { r.Y = y; r.H = r.Height(_sc); y += r.H; }
        _contentHeight = y + Pad;
    }

    private void Place()
    {
        int width = (int)(360 * _sc);
        Rectangle wa = Screen.FromRectangle(_anchor).WorkingArea;
        int desired = (int)(HeaderH + _contentHeight);
        int height = Math.Min(desired, wa.Height - (int)(16 * _sc));
        int x = Math.Max(wa.Left + 8, Math.Min(_anchor.Right - width, wa.Right - width - 8));
        int y = Math.Max(wa.Top + 8, _anchor.Top - height - (int)(10 * _sc));
        SetBounds(x, y, width, height);
    }

    private float ViewH => Height - HeaderH;
    private float MaxScroll => Math.Max(0, _contentHeight - ViewH);

    // --- input ---

    internal void Save() => _s.Save();

    internal void PickColor(Func<Color> get, Action<Color> set)
    {
        using var dlg = new ColorDialog { Color = get(), FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) { set(dlg.Color); _s.Save(); Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxScroll <= 0) return;
        _scroll = Math.Clamp(_scroll - e.Delta * 0.5f, 0, MaxScroll);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    private bool TryRowAt(Point p, out Row row, out PointF local)
    {
        row = null!; local = default;
        if (p.Y < HeaderH) return false;
        float cy = p.Y - HeaderH + _scroll;
        foreach (Row r in _rows)
        {
            if (cy >= r.Y && cy < r.Y + r.H)
            {
                row = r;
                // Content-space point: rows compute their hit rects in the same space
                // they paint in (absolute row.Y + window X), so hand it the matching point.
                local = new PointF(p.X, cy);
                return true;
            }
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool wasHot = _closeHot;
        _closeHot = _closeRect.Contains(e.Location);
        if (_closeHot != wasHot) Invalidate(_closeRect.ToRect());

        bool hand = _closeHot;
        if (!hand && TryRowAt(e.Location, out Row row, out PointF local))
            hand = row.Hand(this, local);
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location)) { Close(); return; }
        if (TryRowAt(e.Location, out Row row, out PointF local) && row.OnClick(this, local))
            Invalidate();
    }

    // --- paint ---

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Base);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Header: title + close.
        using (var fTitle = new Font("Segoe UI", 13f * _sc, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var inkB = new SolidBrush(Ink))
            g.DrawString("Settings", fTitle, inkB, Pad, HeaderH / 2 - 9 * _sc);

        float cs = 30 * _sc;
        _closeRect = new RectangleF(Width - cs - 6 * _sc, (HeaderH - cs) / 2, cs, cs);
        if (_closeHot)
            using (var hot = new SolidBrush(Faint)) Fill(g, _closeRect, hot, 6 * _sc);
        using (var fIcon = Icons.GetFont(12 * _sc))
        using (var cb = new SolidBrush(_closeHot ? Ink : Mut))
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Icons.Close, fIcon, cb, _closeRect, fmt);
        }
        using (var pen = new Pen(Faint)) g.DrawLine(pen, Pad, HeaderH, Width - Pad, HeaderH);

        // Content (clipped + scrolled).
        var view = new RectangleF(0, HeaderH, Width, ViewH);
        g.SetClip(view);
        var state = g.Save();
        g.TranslateTransform(0, HeaderH - _scroll);
        foreach (Row r in _rows)
        {
            if (r.Y + r.H < _scroll || r.Y > _scroll + ViewH) continue; // cull off-screen
            try { r.Paint(g, this); }
            catch { /* one misbehaving row must never white-screen the whole panel */ }
        }
        g.Restore(state);
        g.ResetClip();

        // Scroll indicator.
        if (MaxScroll > 0)
        {
            float track = ViewH, thumb = Math.Max(24 * _sc, track * (ViewH / _contentHeight));
            float ty = HeaderH + (track - thumb) * (_scroll / MaxScroll);
            using var tb = new SolidBrush(Color.FromArgb(90, Mut));
            Fill(g, new RectangleF(Width - 4 * _sc, ty, 3 * _sc, thumb), tb, 1.5f * _sc);
        }
    }

    internal float Sc => _sc;
    internal float RowPad => Pad;

    internal static void Fill(Graphics g, RectangleF r, Brush b, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        if (radius < 0.5f) { g.FillRectangle(b, r); return; }
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    // --- launch at login ---

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
            if (enabled) key?.SetValue("Tallybar", $"\"{Application.ExecutablePath}\"");
            else key?.DeleteValue("Tallybar", throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_open == this) _open = null;
        base.OnFormClosed(e);
    }

    // ===== rows =====

    private abstract class Row
    {
        public float Y, H;
        public abstract float Height(float s);
        public abstract void Paint(Graphics g, SettingsWindow w);
        public virtual bool OnClick(SettingsWindow w, PointF p) => false;
        public virtual bool Hand(SettingsWindow w, PointF p) => false;

        protected void PaintLabel(Graphics g, SettingsWindow w, string text)
        {
            using var f = new Font("Segoe UI", 12f * w.Sc, FontStyle.Regular, GraphicsUnit.Pixel);
            using var b = new SolidBrush(w.Ink);
            g.DrawString(text, f, b, w.RowPad, Y + (H - f.Height) / 2);
        }
    }

    private sealed class Section(string title) : Row
    {
        public override float Height(float s) => 30 * s;
        public override void Paint(Graphics g, SettingsWindow w)
        {
            using var f = new Font("Segoe UI", 8.5f * w.Sc, FontStyle.Bold, GraphicsUnit.Pixel);
            using var b = new SolidBrush(w.Mut);
            var fmt = new StringFormat();
            g.DrawString(title.ToUpperInvariant() + "  ", f, b, w.RowPad, Y + 12 * w.Sc);
            float tw = g.MeasureString(title.ToUpperInvariant() + "  ", f).Width;
            using var pen = new Pen(w.Faint);
            g.DrawLine(pen, w.RowPad + tw, Y + 18 * w.Sc, w.Width - w.RowPad, Y + 18 * w.Sc);
        }
    }

    private sealed class Toggle(string label, Func<bool> get, Action<bool> set, bool save = true) : Row
    {
        public override float Height(float s) => 34 * s;

        private RectangleF Switch(SettingsWindow w)
        {
            float tw = 38 * w.Sc, th = 20 * w.Sc;
            return new RectangleF(w.Width - w.RowPad - tw, Y + (H - th) / 2, tw, th);
        }

        public override void Paint(Graphics g, SettingsWindow w)
        {
            PaintLabel(g, w, label);
            RectangleF sw = Switch(w);
            bool on = get();
            Color track = on ? w.Accent : Color.FromArgb(w.Base == Color.White ? 210 : 70, w.Mut);
            using (var tb = new SolidBrush(track)) Fill(g, sw, tb, sw.Height / 2);
            float knob = sw.Height - 4 * w.Sc;
            float kx = on ? sw.Right - knob - 2 * w.Sc : sw.Left + 2 * w.Sc;
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx, sw.Top + 2 * w.Sc, knob, knob);
        }

        public override bool OnClick(SettingsWindow w, PointF p)
        {
            set(!get());
            if (save) w.Save();
            return true;
        }

        public override bool Hand(SettingsWindow w, PointF p) => true;
    }

    private sealed class Segment(string label, string[] options, Func<int> getIndex, Action<int> setIndex) : Row
    {
        private readonly RectangleF[] _rects = new RectangleF[options.Length];

        public override float Height(float s) => 34 * s;

        private void Compute(SettingsWindow w, Graphics? g)
        {
            using var f = new Font("Segoe UI", 11f * w.Sc, FontStyle.Regular, GraphicsUnit.Pixel);
            float padX = 9 * w.Sc, h = 22 * w.Sc, gap = 3 * w.Sc;

            // Measure with the caller's Graphics when painting, or a throwaway one when
            // hit-testing. Never dispose the caller's Graphics — it belongs to OnPaint.
            Bitmap? tmp = null;
            Graphics mg = g ?? Graphics.FromImage(tmp = new Bitmap(1, 1));
            try
            {
                float total = 0;
                var widths = new float[options.Length];
                for (int i = 0; i < options.Length; i++)
                {
                    widths[i] = mg.MeasureString(options[i], f).Width + padX * 2;
                    total += widths[i] + (i > 0 ? gap : 0);
                }
                float x = w.Width - w.RowPad - total, y = Y + (H - h) / 2;
                for (int i = 0; i < options.Length; i++)
                {
                    _rects[i] = new RectangleF(x, y, widths[i], h);
                    x += widths[i] + gap;
                }
            }
            finally
            {
                if (tmp is not null) { mg.Dispose(); tmp.Dispose(); }
            }
        }

        public override void Paint(Graphics g, SettingsWindow w)
        {
            PaintLabel(g, w, label);
            Compute(w, g);
            int sel = getIndex();
            using var f = new Font("Segoe UI", 11f * w.Sc, FontStyle.Regular, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < options.Length; i++)
            {
                bool on = i == sel;
                using (var bg = new SolidBrush(on ? w.Accent : w.Faint))
                    Fill(g, _rects[i], bg, _rects[i].Height / 2);
                Color tc = on ? PickTextOn(w.Accent) : w.Mut;
                using var tb = new SolidBrush(tc);
                g.DrawString(options[i], f, tb, _rects[i], fmt);
            }
        }

        private static Color PickTextOn(Color accent)
            => accent.GetBrightness() > 0.6 ? Color.FromArgb(18, 22, 28) : Color.White;

        public override bool OnClick(SettingsWindow w, PointF p)
        {
            Compute(w, null);
            for (int i = 0; i < _rects.Length; i++)
                if (_rects[i].Contains(p)) { setIndex(i); w.Save(); return true; }
            return false;
        }

        public override bool Hand(SettingsWindow w, PointF p)
        {
            Compute(w, null);
            return _rects.Any(r => r.Contains(p));
        }
    }

    private sealed class Stepper(
        string label, int min, int max, int step,
        Func<int> get, Action<int> set, Func<int, string> format) : Row
    {
        private RectangleF _minus, _plus;

        public override float Height(float s) => 34 * s;

        private void Compute(SettingsWindow w)
        {
            float bs = 24 * w.Sc, y = Y + (H - bs) / 2;
            _plus = new RectangleF(w.Width - w.RowPad - bs, y, bs, bs);
            _minus = new RectangleF(_plus.Left - 66 * w.Sc, y, bs, bs);
        }

        public override void Paint(Graphics g, SettingsWindow w)
        {
            PaintLabel(g, w, label);
            Compute(w);
            using var fIcon = Icons.GetFont(11 * w.Sc);
            using var fVal = new Font("Segoe UI", 12f * w.Sc, FontStyle.Regular, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            foreach ((RectangleF r, string glyph, bool enabled) in
                     new[] { (_minus, Icons.Remove, get() > min), (_plus, Icons.Add, get() < max) })
            {
                using var bg = new SolidBrush(w.Faint);
                Fill(g, r, bg, 6 * w.Sc);
                using var gb = new SolidBrush(enabled ? w.Ink : w.Mut);
                g.DrawString(glyph, fIcon, gb, r, fmt);
            }

            using var vb = new SolidBrush(w.Accent);
            var valRect = new RectangleF(_minus.Right, _minus.Top, _plus.Left - _minus.Right, _minus.Height);
            g.DrawString(format(get()), fVal, vb, valRect, fmt);
        }

        public override bool OnClick(SettingsWindow w, PointF p)
        {
            Compute(w);
            int v = get();
            if (_minus.Contains(p)) v = Math.Max(min, v - step);
            else if (_plus.Contains(p)) v = Math.Min(max, v + step);
            else return false;
            set(v);
            w.Save();
            return true;
        }

        public override bool Hand(SettingsWindow w, PointF p)
        {
            Compute(w);
            return _minus.Contains(p) || _plus.Contains(p);
        }
    }

    private sealed class ColorSwatch(string label, Func<Color> get, Action<Color> set) : Row
    {
        private RectangleF _swatch;

        public override float Height(float s) => 34 * s;

        private void Compute(SettingsWindow w)
        {
            float sw = 46 * w.Sc, sh = 22 * w.Sc;
            _swatch = new RectangleF(w.Width - w.RowPad - sw, Y + (H - sh) / 2, sw, sh);
        }

        public override void Paint(Graphics g, SettingsWindow w)
        {
            PaintLabel(g, w, label);
            Compute(w);
            using (var b = new SolidBrush(get())) Fill(g, _swatch, b, 6 * w.Sc);
            using var pen = new Pen(Color.FromArgb(70, w.Mut));
            using var path = RoundedPath(_swatch, 6 * w.Sc);
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

        public override bool OnClick(SettingsWindow w, PointF p)
        {
            Compute(w);
            if (_swatch.Contains(p)) { w.PickColor(get, set); }
            return false; // PickColor repaints itself
        }

        public override bool Hand(SettingsWindow w, PointF p)
        {
            Compute(w);
            return _swatch.Contains(p);
        }
    }
}

internal static class RectFExtensions
{
    public static Rectangle ToRect(this System.Drawing.RectangleF r)
        => Rectangle.Round(r);
}
