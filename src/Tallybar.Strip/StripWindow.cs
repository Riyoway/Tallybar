using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tallybar;

/// <summary>
/// The taskbar strip: a borderless, per-pixel-alpha, topmost overlay window glued to a
/// taskbar just left of the clock. Renders a live sparkline + usage numbers driven by
/// Settings (items, colors, thresholds, animation), cycles between providers, opens
/// the popover on click, and can be dragged to move / edge-dragged to resize.
/// The primary strip also owns the tray icon, rendered as a live mini-gauge.
/// </summary>
internal sealed class StripWindow : Form
{
    private readonly Settings _settings;
    private readonly Poller _poller;
    private readonly int _taskbarIndex;
    private readonly bool _isPrimary;

    private readonly int _taskbarCreatedMsg;
    private readonly Native.WinEventDelegate _winEventCallback; // held to prevent GC
    private readonly System.Windows.Forms.Timer _reassert;
    private readonly System.Windows.Forms.Timer _cycle;
    private readonly System.Windows.Forms.Timer _anim;
    private IntPtr _winEventHook;
    private NotifyIcon? _tray;
    private IntPtr _trayIconHandle;
    private bool _hiddenForFullscreen;

    /// <summary>Raised on WM_DISPLAYCHANGE so the owner can rebuild secondary strips.</summary>
    public event Action? DisplayChanged;

    private double _scale = 1.0; // last DPI scale seen, for converting drag deltas
    private DragMode _drag = DragMode.None;
    private Point _dragStartMouse;
    private int _dragStartGap;
    private int _dragStartWidth;
    private int _dragStartHeight;
    private bool _dragMoved;

    private enum DragMode { None, Move, ResizeWidth, ResizeHeight }

    // What we draw.
    private int _providerIndex;
    private string _providerId = "";
    private string _label = "Tallybar";
    private double _fraction = double.NaN;   // target value from the poller
    private double _shownFraction = double.NaN; // eased value actually painted
    private string _subText = "connecting…";
    private FetchStatus _status = FetchStatus.Offline;

    public StripWindow(Settings settings, Poller poller, int taskbarIndex = 0, bool isPrimary = true)
    {
        _settings = settings;
        _poller = poller;
        _taskbarIndex = taskbarIndex;
        _isPrimary = isPrimary;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Tallybar";
        Size = new Size(_settings.Width, _settings.Height);

        _taskbarCreatedMsg = (int)Native.RegisterWindowMessageW("TaskbarCreated");
        _winEventCallback = OnTrayLocationChanged;

        // A 1 Hz safety net catches anything the WinEvent hook misses and doubles as
        // the fullscreen-state watcher. Cheap (one small layered repaint); revisit if
        // it ever shows up in a battery trace.
        _reassert = new System.Windows.Forms.Timer { Interval = 1000 };
        _reassert.Tick += (_, _) => Reposition();

        _cycle = new System.Windows.Forms.Timer { Interval = _settings.CycleSeconds * 1000 };
        _cycle.Tick += (_, _) => AdvanceProvider();

        // Animation clock: eases the shown value and pulses the endpoint when critical.
        // Runs only while there is something to animate.
        _anim = new System.Windows.Forms.Timer { Interval = 40 };
        _anim.Tick += (_, _) => AnimateTick();

        _poller.Updated += RefreshFromPoller;
        _settings.Changed += OnSettingsChanged;
    }

    // --- data selection ---

    private List<IProvider> ActiveProviders() =>
        [.. _poller.Providers.Where(p => _settings.IsProviderEnabled(p.Id) && p.IsConfigured)];

    private void AdvanceProvider()
    {
        var active = ActiveProviders();
        if (active.Count < 2) return;
        _providerIndex = (_providerIndex + 1) % active.Count;
        RefreshFromPoller();
    }

    private void RefreshFromPoller()
    {
        var active = ActiveProviders();
        IProvider? current = null;
        if (_settings.StripProvider != "cycle")
            current = active.FirstOrDefault(p => p.Id == _settings.StripProvider);
        if (current is null && active.Count > 0)
            current = active[Math.Min(_providerIndex, active.Count - 1)];

        if (current is null)
        {
            _providerId = "";
            _label = "Tallybar";
            _fraction = double.NaN;
            _status = FetchStatus.NotConfigured;
            _subText = "no providers";
            Reposition();
            return;
        }

        bool providerChanged = _providerId != current.Id;
        _providerId = current.Id;
        _label = current.DisplayName;
        var snapshots = _poller.Latest(current.Id);
        UsageSnapshot? primary = snapshots.FirstOrDefault();
        _status = primary?.Status ?? FetchStatus.Offline;
        _fraction = primary?.Fraction ?? double.NaN;
        // Never ease across providers or into missing data — that would show the
        // previous provider's number under the new provider's name.
        if (providerChanged || double.IsNaN(_fraction) || double.IsNaN(_shownFraction))
            _shownFraction = _fraction;

        if (primary is { Status: FetchStatus.Ok or FetchStatus.Stale } p && !double.IsNaN(p.Fraction))
        {
            string countdown = p.ResetsAt is { } r ? "↻ " + FormatCountdown(r) : "";
            UsageSnapshot? weekly = snapshots.Skip(1).FirstOrDefault(s => !double.IsNaN(s.Fraction));
            string wk = weekly is { } w ? $"wk {(int)Math.Round(w.Fraction * 100)}%" : "";
            _subText = (countdown, wk) switch
            {
                ("", "") => p.WindowLabel,
                ("", _) => wk,
                (_, "") => countdown,
                _ => $"{countdown} · {wk}",
            };
            if (p.Status == FetchStatus.Stale) _subText += " · stale";
        }
        else
        {
            _subText = _status switch
            {
                FetchStatus.AuthError => "sign-in needed",
                FetchStatus.NotConfigured => "not configured",
                _ => "offline",
            };
        }

        UpdateTrayGauge();
        UpdateAnimTimer();
        Reposition();
    }

    private void OnSettingsChanged()
    {
        _cycle.Interval = _settings.CycleSeconds * 1000;
        RefreshFromPoller();
    }

    private static string FormatCountdown(DateTimeOffset resetsAt)
    {
        TimeSpan t = resetsAt - DateTimeOffset.UtcNow;
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays}d{t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        return $"{t.Minutes}m";
    }

    // --- animation ---

    private bool NeedsPulse =>
        _settings.Animations && _status == FetchStatus.Ok &&
        !double.IsNaN(_fraction) && _fraction >= _settings.CritAt;

    private bool NeedsEase =>
        _settings.Animations && !double.IsNaN(_fraction) && !double.IsNaN(_shownFraction) &&
        Math.Abs(_fraction - _shownFraction) > 0.002;

    private void UpdateAnimTimer()
    {
        bool need = NeedsPulse || NeedsEase;
        if (need && !_anim.Enabled) _anim.Start();
        else if (!need && _anim.Enabled) _anim.Stop();
        if (!_settings.Animations) _shownFraction = _fraction;
    }

    private void AnimateTick()
    {
        if (NeedsEase)
            _shownFraction += (_fraction - _shownFraction) * 0.2;
        else
            _shownFraction = _fraction;
        Reposition();
        UpdateAnimTimer();
    }

    // --- window plumbing ---

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    // Never take focus from the taskbar / foreground app.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HookTrayThread();
        if (_isPrimary) SetupTray();
        RefreshFromPoller();
        _reassert.Start();
        _cycle.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreatedMsg)
        {
            // Explorer (re)started — the old tray HWND/thread is gone. Re-hook and re-glue.
            RehookAndReposition();
        }
        else
        {
            switch (m.Msg)
            {
                case Native.WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    Reposition();
                    break;
                case Native.WM_DPICHANGED:
                case Native.WM_SETTINGCHANGE:
                    Reposition();
                    break;
            }
        }
        base.WndProc(ref m);
    }

    private void HookTrayThread()
    {
        IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        uint threadId = Native.GetWindowThreadProcessId(tray, out uint processId);
        _winEventHook = Native.SetWinEventHook(
            Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventCallback, processId, threadId, Native.WINEVENT_OUTOFCONTEXT);
    }

    private void RehookAndReposition()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        HookTrayThread();
        Reposition();
    }

    private void OnTrayLocationChanged(
        IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => Reposition();

    // --- mouse: body = move / click for popover; left/top edge = resize ---

    private DragMode HitTest(Point client)
    {
        int edge = Math.Max(5, (int)(6 * _scale));
        if (client.X <= edge) return DragMode.ResizeWidth;
        if (client.Y <= edge / 2 + 2) return DragMode.ResizeHeight;
        return DragMode.Move;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drag = HitTest(e.Location);
        _dragMoved = false;
        _dragStartMouse = Control.MousePosition; // physical px (Per-Monitor-V2)
        _dragStartGap = _settings.Gap;
        _dragStartWidth = _settings.Width;
        _dragStartHeight = _settings.Height;
        Capture = true; // keep receiving moves even off the strip
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None)
        {
            Cursor = HitTest(e.Location) switch
            {
                DragMode.ResizeWidth => Cursors.SizeWE,
                DragMode.ResizeHeight => Cursors.SizeNS,
                _ => Cursors.Hand,
            };
            return;
        }

        Point mouse = Control.MousePosition;
        int dx = _dragStartMouse.X - mouse.X; // >0 when dragging left
        int dy = _dragStartMouse.Y - mouse.Y; // >0 when dragging up
        if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) _dragMoved = true;
        switch (_drag)
        {
            case DragMode.Move:
                _settings.Gap = Math.Max(0, _dragStartGap + dx);
                break;
            case DragMode.ResizeWidth:
                // Right edge stays anchored; pulling the left edge outward widens.
                _settings.Width = Math.Clamp(
                    _dragStartWidth + (int)(dx / _scale), Settings.MinWidth, Settings.MaxWidth);
                break;
            case DragMode.ResizeHeight:
                // Pulling the top edge upward grows the strip (taskbar height still caps it).
                _settings.Height = Math.Clamp(
                    _dragStartHeight + (int)(dy / _scale), Settings.MinHeight, Settings.MaxHeight);
                break;
        }
        Reposition();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        bool wasClick = _drag == DragMode.Move && !_dragMoved && e.Button == MouseButtons.Left;
        _drag = DragMode.None;
        Capture = false;
        if (wasClick)
            PopoverWindow.Toggle(_settings, _poller, RectangleToScreen(ClientRectangle));
        else
            _settings.Save();
    }

    private void ResetLayout()
    {
        var fresh = new Settings();
        _settings.Gap = fresh.Gap;
        _settings.Width = fresh.Width;
        _settings.Height = fresh.Height;
        _settings.Save();
        Reposition();
    }

    // --- placement + paint ---

    private void Reposition()
    {
        // Mirror the taskbar's behavior: get out of the way of fullscreen apps.
        if (IsFullscreenActive())
        {
            if (!_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                Native.ShowWindow(Handle, Native.SW_HIDE);
            }
            return;
        }
        if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Native.ShowWindow(Handle, Native.SW_SHOWNA);
        }

        if (!TaskbarLocator.TryGet(_taskbarIndex, out var p))
        {
            // Taskbar gone (unknown shell layout / monitor removed); the tray gauge
            // keeps working, so just stay hidden until it reappears.
            Native.ShowWindow(Handle, Native.SW_HIDE);
            return;
        }

        _scale = p.Scale;
        int height = Math.Min(p.TaskbarRect.Height - 8, (int)(_settings.Height * p.Scale));
        if (height < 16) height = Math.Max(8, p.TaskbarRect.Height - 4);
        int width = (int)(_settings.Width * p.Scale);
        int margin = (int)(8 * p.Scale);

        // Anchor just left of the clock, then shift further left by the user's drag gap so
        // the strip can be parked in empty space instead of overlapping tray icons.
        int x = p.ClockRect.Left - width - margin - _settings.Gap;
        x = Math.Max(p.TaskbarRect.Left, x); // never run off the left edge
        int y = p.TaskbarRect.Top + (p.TaskbarRect.Height - height) / 2;

        using Bitmap frame = Render(width, height, p.Scale);
        PushLayered(frame, x, y, (byte)(255 * _settings.Opacity));

        // Re-assert topmost without moving/resizing (the taskbar jumps above us on Win-key / hover).
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static bool IsFullscreenActive()
    {
        if (Native.SHQueryUserNotificationState(out int state) != 0) return false;
        return state is Native.QUNS_BUSY
                     or Native.QUNS_RUNNING_D3D_FULL_SCREEN
                     or Native.QUNS_PRESENTATION_MODE;
    }

    internal static bool IsLightTheme(Settings settings)
    {
        if (settings.Theme == "dark") return false;
        if (settings.Theme == "light") return true;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is 1;
        }
        catch { return false; }
    }

    private Color StateColor() => _status switch
    {
        FetchStatus.Ok or FetchStatus.Stale when !double.IsNaN(_fraction)
            => _settings.ColorFor(_fraction),
        _ => Color.FromArgb(120, 128, 140), // stale / offline gray
    };

    private Bitmap Render(int width, int height, double scale)
    {
        bool light = IsLightTheme(_settings);
        Color tone = StateColor();
        bool dim = _status is not FetchStatus.Ok;
        double shown = double.IsNaN(_shownFraction) ? _fraction : _shownFraction;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // Glassy pill: translucent fill + hairline edge, tinted for the current theme.
        var rect = new Rectangle(0, 0, width - 1, height - 1);
        using GraphicsPath pill = RoundedRect(rect, (int)(_settings.CornerRadius * scale));
        using (var fill = new SolidBrush(light
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(60, 20, 24, 32)))
        using (var edge = new Pen(Color.FromArgb(dim ? 60 : 110, tone), 1f))
        {
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
        }

        int pad = (int)(8 * scale);
        float x = pad;

        if (_settings.ShowSparkline)
        {
            var sparkRect = new Rectangle(pad, pad, (int)(width * 0.42) - pad, height - pad * 2);
            DrawSparkline(g, sparkRect, tone, dim);
            x = sparkRect.Right + pad;
        }

        Color ink = light ? Color.FromArgb(230, 20, 24, 32) : Color.FromArgb(235, 233, 236, 244);
        Color inkMut = Color.FromArgb(dim ? 120 : 160, ink);
        if (dim) ink = Color.FromArgb(150, ink);

        float w = width - x - pad;
        if (w > 8)
        {
            string pct = double.IsNaN(shown) ? "—" : $"{(int)Math.Round(shown * 100)}%";
            bool twoLines = _settings.ShowSubline;
            float f1Px = (float)(height * (twoLines ? 0.30 : 0.40));

            using var f1 = new Font("Segoe UI", f1Px, FontStyle.Bold, GraphicsUnit.Pixel);
            using var f2 = new Font("Segoe UI", (float)(height * 0.24), FontStyle.Regular, GraphicsUnit.Pixel);
            using var inkBrush = new SolidBrush(ink);
            using var toneBrush = new SolidBrush(dim ? inkMut : Color.FromArgb(235, tone));
            using var mutBrush = new SolidBrush(inkMut);
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

            float line1Y = twoLines ? height * 0.16f : (height - f1.Height) / 2f;
            float tx = x;
            if (_settings.ShowLabel)
            {
                g.DrawString(_label, f1, inkBrush, new RectangleF(tx, line1Y, w, f1.Height), fmt);
                tx += g.MeasureString(_label, f1, (int)w, fmt).Width;
            }
            if (_settings.ShowPercent)
                g.DrawString(pct, f1, toneBrush, tx, line1Y);
            if (twoLines)
            {
                float sy = height * 0.52f, sx = x;
                string sub = _subText;
                if (sub.StartsWith("↻ ", StringComparison.Ordinal))
                {
                    // Countdown marker as a crisp icon-font clock instead of a text glyph.
                    sub = sub[2..];
                    sx += Icons.Draw(g, Icons.Clock, f2.Size * 0.92f, mutBrush, sx, sy + f2.Size * 0.08f) + 2;
                }
                g.DrawString(sub, f2, mutBrush, new RectangleF(sx, sy, w - (sx - x), f2.Height), fmt);
            }
        }

        return bmp;
    }

    private void DrawSparkline(Graphics g, Rectangle r, Color tone, bool dim)
    {
        Ring history = _providerId.Length > 0 ? _poller.History(_providerId) : new Ring(2);
        Span<float> data = stackalloc float[120];
        int n = history.CopyLatest(data);
        if (n < 2)
        {
            // Not enough history yet: a flat placeholder line at the current value.
            float baseline = double.IsNaN(_fraction) ? 0.5f : (float)_fraction;
            data[0] = baseline; data[1] = baseline;
            n = 2;
        }

        int alpha = dim ? 110 : 235;
        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float v = Math.Clamp(data[i], 0f, 1f);
            pts[i] = new PointF(
                r.Left + i * (float)r.Width / (n - 1),
                r.Bottom - v * r.Height);
        }

        // Soft area fill under the line, then the line, then an endpoint dot.
        var area = new PointF[n + 2];
        pts.CopyTo(area, 0);
        area[n] = new PointF(r.Right, r.Bottom);
        area[n + 1] = new PointF(r.Left, r.Bottom);
        using (var fill = new SolidBrush(Color.FromArgb(dim ? 20 : 45, tone)))
            g.FillPolygon(fill, area);
        using (var pen = new Pen(Color.FromArgb(alpha, tone), 1.6f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);

        // Endpoint dot; pulses gently when the window is nearly exhausted.
        PointF end = pts[n - 1];
        float radius = 2.2f;
        if (NeedsPulse)
        {
            double phase = Environment.TickCount64 % 1200 / 1200.0 * Math.Tau;
            radius += (float)(Math.Sin(phase) * 0.5 + 0.5) * 1.6f;
        }
        using (var dot = new SolidBrush(Color.FromArgb(alpha, tone)))
            g.FillEllipse(dot, end.X - radius, end.Y - radius, radius * 2, radius * 2);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        if (radius < 1)
        {
            var square = new GraphicsPath();
            square.AddRectangle(r);
            return square;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Blit a 32bpp premultiplied bitmap onto the window with per-pixel alpha.</summary>
    private void PushLayered(Bitmap bmp, int x, int y, byte alpha)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0)); // premultiplied ARGB
            oldBitmap = Native.SelectObject(memDc, hBitmap);

            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var dst = new Native.POINT { x = x, y = y };
            var src = new Native.POINT { x = 0, y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            if (oldBitmap != IntPtr.Zero) Native.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
        }
    }

    // --- tray icon: a live mini-gauge, also the fallback when the overlay can't attach ---

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Tallybar — drag to move, drag left/top edge to resize").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) =>
            PopoverWindow.Toggle(_settings, _poller, RectangleToScreen(ClientRectangle)));
        menu.Items.Add("Settings…", null, (_, _) =>
            SettingsWindow.Open(_settings, RectangleToScreen(ClientRectangle)));
        menu.Items.Add("Refresh now", null, (_, _) => _poller.RefreshNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reset position && size", null, (_, _) => ResetLayout());
        menu.Items.Add("Re-attach to taskbar", null, (_, _) => RehookAndReposition());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        Icon? appIcon = null;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _tray = new NotifyIcon
        {
            Text = "Tallybar",
            Icon = appIcon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                PopoverWindow.Toggle(_settings, _poller, RectangleToScreen(ClientRectangle));
        };
        UpdateTrayGauge();
    }

    /// <summary>Redraws the tray icon as horizontal usage bars, one per enabled provider.</summary>
    private void UpdateTrayGauge()
    {
        if (_tray is null) return;

        var rows = new List<(double Fraction, Color Tone, string Tip)>();
        foreach (IProvider p in ActiveProviders())
        {
            UsageSnapshot? s = _poller.Latest(p.Id).FirstOrDefault();
            if (s is null || double.IsNaN(s.Fraction)) continue;
            rows.Add((s.Fraction, _settings.ColorFor(s.Fraction),
                $"{p.DisplayName} {(int)Math.Round(s.Fraction * 100)}%"));
        }

        int size = 32; // scaled down by the shell as needed
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            static void Rounded(Graphics g, Brush b, RectangleF r)
            {
                float d = Math.Min(r.Height, r.Width);
                using var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(b, path);
            }

            if (rows.Count == 0)
            {
                using var pen = new Pen(Color.FromArgb(200, 128, 134, 148), 3f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, 6, 6, size - 12, size - 12, 135, 270); // idle gauge arc
            }
            else
            {
                float barH = Math.Min(8f, (size - 6f) / rows.Count - 4f);
                float y = (size - rows.Count * (barH + 4) + 4) / 2f;
                foreach ((double fraction, Color tone, _) in rows)
                {
                    float w = (size - 6) * (float)Math.Clamp(fraction, 0, 1);
                    using var back = new SolidBrush(Color.FromArgb(60, tone));
                    using var fore = new SolidBrush(tone);
                    Rounded(g, back, new RectangleF(3, y, size - 6, barH));
                    if (w >= barH) Rounded(g, fore, new RectangleF(3, y, w, barH));
                    y += barH + 4;
                }
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            _tray.Icon = (Icon)Icon.FromHandle(hIcon).Clone();
            _tray.Text = rows.Count == 0
                ? "Tallybar"
                : string.Join(" · ", rows.Select(r => r.Tip)) is var tip && tip.Length > 63
                    ? tip[..63] : tip; // NotifyIcon caps tooltips at 63 chars
        }
        finally
        {
            if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
            _trayIconHandle = hIcon;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poller.Updated -= RefreshFromPoller;
            _settings.Changed -= OnSettingsChanged;
            _reassert.Stop(); _reassert.Dispose();
            _cycle.Stop(); _cycle.Dispose();
            _anim.Stop(); _anim.Dispose();
            if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
            _tray?.Dispose();
            if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        }
        base.Dispose(disposing);
    }
}
