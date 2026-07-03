using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tallybar;

/// <summary>
/// The taskbar strip: a borderless, per-pixel-alpha, topmost overlay window glued to the
/// primary taskbar just left of the clock. Renders a live sparkline + usage numbers,
/// follows theme changes, hides over fullscreen apps, and can be dragged along the
/// taskbar to avoid covering other icons (position persists).
/// </summary>
internal sealed class StripWindow : Form
{
    private readonly int _taskbarCreatedMsg;
    private readonly Native.WinEventDelegate _winEventCallback; // held to prevent GC
    private readonly System.Windows.Forms.Timer _reassert;
    private IntPtr _winEventHook;
    private NotifyIcon? _tray;
    private bool _hiddenForFullscreen;

    // Position + size, persisted. Drag the body to move; drag the left/top edge to resize.
    private readonly StripLayout _layout;
    private double _scale = 1.0; // last DPI scale seen, for converting drag deltas
    private DragMode _drag = DragMode.None;
    private Point _dragStartMouse;
    private int _dragStartGap;
    private int _dragStartWidth;
    private int _dragStartHeight;

    private enum DragMode { None, Move, ResizeWidth, ResizeHeight }

    // What we draw (fed by the poller via ShowUsage).
    private readonly Ring _history = new(48);
    private string _label = "Tallybar";
    private double _fraction = double.NaN;
    private string _subText = "connecting…";
    private FetchStatus _status = FetchStatus.Offline;

    public StripWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Tallybar";
        _layout = StripLayout.Load();
        Size = new Size(_layout.Width, _layout.Height);

        _taskbarCreatedMsg = (int)Native.RegisterWindowMessageW("TaskbarCreated");
        _winEventCallback = OnTrayLocationChanged;

        // A 1 Hz safety net catches anything the WinEvent hook misses and doubles as
        // the fullscreen-state watcher. Cheap (one small layered repaint); revisit if
        // it ever shows up in a battery trace.
        _reassert = new System.Windows.Forms.Timer { Interval = 1000 };
        _reassert.Tick += (_, _) => Reposition();
    }

    /// <summary>Push the latest usage data onto the strip (UI thread).</summary>
    public void ShowUsage(string label, IReadOnlyList<UsageSnapshot> snapshots)
    {
        UsageSnapshot? primary = snapshots.FirstOrDefault();
        _label = label;
        _status = primary?.Status ?? FetchStatus.Offline;
        _fraction = primary?.Fraction ?? double.NaN;

        if (primary is { Status: FetchStatus.Ok or FetchStatus.Stale } p && !double.IsNaN(p.Fraction))
        {
            _history.Push((float)p.Fraction);
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
        Reposition();
    }

    private static string FormatCountdown(DateTimeOffset resetsAt)
    {
        TimeSpan t = resetsAt - DateTimeOffset.UtcNow;
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays}d{t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        return $"{t.Minutes}m";
    }

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
        SetupTray();
        Reposition();
        _reassert.Start();
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

    // --- drag: body = move along the taskbar; left/top edge = resize ---

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
        _dragStartMouse = Control.MousePosition; // physical px (Per-Monitor-V2)
        _dragStartGap = _layout.Gap;
        _dragStartWidth = _layout.Width;
        _dragStartHeight = _layout.Height;
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
                _ => Cursors.SizeAll,
            };
            return;
        }

        Point mouse = Control.MousePosition;
        int dx = _dragStartMouse.X - mouse.X; // >0 when dragging left
        int dy = _dragStartMouse.Y - mouse.Y; // >0 when dragging up
        switch (_drag)
        {
            case DragMode.Move:
                _layout.Gap = Math.Max(0, _dragStartGap + dx);
                break;
            case DragMode.ResizeWidth:
                // Right edge stays anchored; pulling the left edge outward widens.
                _layout.Width = Math.Clamp(
                    _dragStartWidth + (int)(dx / _scale), StripLayout.MinWidth, StripLayout.MaxWidth);
                break;
            case DragMode.ResizeHeight:
                // Pulling the top edge upward grows the strip (taskbar height still caps it).
                _layout.Height = Math.Clamp(
                    _dragStartHeight + (int)(dy / _scale), StripLayout.MinHeight, StripLayout.MaxHeight);
                break;
        }
        Reposition();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        Capture = false;
        _layout.Save();
    }

    private void ResetLayout()
    {
        var fresh = new StripLayout();
        _layout.Gap = fresh.Gap;
        _layout.Width = fresh.Width;
        _layout.Height = fresh.Height;
        _layout.Save();
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

        if (!TaskbarLocator.TryGet(out var p))
        {
            // No taskbar found (unknown shell layout); stay hidden until it reappears.
            Native.ShowWindow(Handle, Native.SW_HIDE);
            return;
        }

        _scale = p.Scale;
        int height = Math.Min(p.TaskbarRect.Height - 8, (int)(_layout.Height * p.Scale));
        if (height < 16) height = Math.Max(8, p.TaskbarRect.Height - 4);
        int width = (int)(_layout.Width * p.Scale);
        int margin = (int)(8 * p.Scale);

        // Anchor just left of the clock, then shift further left by the user's drag gap so
        // the strip can be parked in empty space instead of overlapping tray icons.
        int x = p.ClockRect.Left - width - margin - _layout.Gap;
        x = Math.Max(p.TaskbarRect.Left, x); // never run off the left edge
        int y = p.TaskbarRect.Top + (p.TaskbarRect.Height - height) / 2;

        using Bitmap frame = Render(width, height, p.Scale);
        PushLayered(frame, x, y);

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

    private static bool IsLightTaskbar()
    {
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
        FetchStatus.Ok or FetchStatus.Stale when !double.IsNaN(_fraction) => _fraction switch
        {
            < 0.75 => Color.FromArgb(79, 214, 147),   // healthy green
            < 0.90 => Color.FromArgb(255, 207, 107),  // tightening amber
            _ => Color.FromArgb(255, 111, 122),       // near-limit red
        },
        _ => Color.FromArgb(120, 128, 140),           // stale / offline gray
    };

    private Bitmap Render(int width, int height, double scale)
    {
        bool light = IsLightTaskbar();
        Color tone = StateColor();
        bool dim = _status is not FetchStatus.Ok;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // Glassy pill: translucent fill + hairline edge, tinted for the current theme.
        var rect = new Rectangle(0, 0, width - 1, height - 1);
        using GraphicsPath pill = RoundedRect(rect, (int)(9 * scale));
        using (var fill = new SolidBrush(light
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(60, 20, 24, 32)))
        using (var edge = new Pen(Color.FromArgb(dim ? 60 : 110, tone), 1f))
        {
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
        }

        int pad = (int)(8 * scale);

        // Sparkline on the left ~45%.
        var sparkRect = new Rectangle(pad, pad, (int)(width * 0.42) - pad, height - pad * 2);
        DrawSparkline(g, sparkRect, tone, dim);

        // Text on the right: "Claude 62%" over "↻ 3h42m · wk 78%".
        Color ink = light ? Color.FromArgb(230, 20, 24, 32) : Color.FromArgb(235, 233, 236, 244);
        Color inkMut = Color.FromArgb(dim ? 120 : 160, ink);
        if (dim) ink = Color.FromArgb(150, ink);

        float x = sparkRect.Right + pad;
        float w = width - x - pad;
        string pct = double.IsNaN(_fraction) ? "—" : $"{(int)Math.Round(_fraction * 100)}%";

        using var f1 = new Font("Segoe UI", (float)(height * 0.30), FontStyle.Bold, GraphicsUnit.Pixel);
        using var f2 = new Font("Segoe UI", (float)(height * 0.24), FontStyle.Regular, GraphicsUnit.Pixel);
        using var inkBrush = new SolidBrush(ink);
        using var toneBrush = new SolidBrush(dim ? inkMut : Color.FromArgb(235, tone));
        using var mutBrush = new SolidBrush(inkMut);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        float line1Y = height * 0.16f;
        g.DrawString(_label, f1, inkBrush, new RectangleF(x, line1Y, w, f1.Height), fmt);
        SizeF labelSize = g.MeasureString(_label, f1, (int)w, fmt);
        g.DrawString(pct, f1, toneBrush, x + labelSize.Width, line1Y);
        g.DrawString(_subText, f2, mutBrush, new RectangleF(x, height * 0.52f, w, f2.Height), fmt);

        return bmp;
    }

    private void DrawSparkline(Graphics g, Rectangle r, Color tone, bool dim)
    {
        Span<float> data = stackalloc float[48];
        int n = _history.CopyLatest(data);
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
        PointF end = pts[n - 1];
        using (var dot = new SolidBrush(Color.FromArgb(alpha, tone)))
            g.FillEllipse(dot, end.X - 2.2f, end.Y - 2.2f, 4.4f, 4.4f);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Blit a 32bpp premultiplied bitmap onto the window with per-pixel alpha.</summary>
    private void PushLayered(Bitmap bmp, int x, int y)
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
                SourceConstantAlpha = 255,
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

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Tallybar — drag to move, drag left/top edge to resize").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reset position && size", null, (_, _) => ResetLayout());
        menu.Items.Add("Re-attach to taskbar", null, (_, _) => RehookAndReposition());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _tray = new NotifyIcon
        {
            Text = "Tallybar",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reassert.Stop();
            _reassert.Dispose();
            if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
            _tray?.Dispose();
        }
        base.Dispose(disposing);
    }
}
