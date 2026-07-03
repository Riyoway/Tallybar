using System.Drawing;
using System.Text;

namespace Tallybar;

/// <summary>
/// Finds taskbars and the tray/clock zone so a strip can dock just left of the clock.
/// Index 0 is the primary taskbar (Shell_TrayWnd); 1..n are secondary-monitor taskbars
/// (Shell_SecondaryTrayWnd), ordered left-to-right for stable identity across polls.
/// All rects are physical pixels (process is Per-Monitor-V2 aware).
///
/// Horizontal (bottom/top) taskbars only; vertical taskbars are not handled yet.
/// </summary>
internal static class TaskbarLocator
{
    public readonly record struct Placement(Rectangle TaskbarRect, Rectangle ClockRect, double Scale);

    public static int Count() => 1 + Secondaries().Count;

    public static bool TryGet(int index, out Placement placement)
    {
        placement = default;

        IntPtr tray;
        if (index == 0)
        {
            tray = Native.FindWindowW("Shell_TrayWnd", null);
        }
        else
        {
            var secondaries = Secondaries();
            if (index - 1 >= secondaries.Count) return false;
            tray = secondaries[index - 1];
        }
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var tb))
            return false;

        Rectangle taskbar = tb.ToRectangle();

        // TrayNotifyWnd (clock + system tray) exists on both Win10 and Win11 and is the
        // one stable right-side anchor. App icons are unreliable (Win11 centers them).
        // Secondary taskbars may have no clock; fall back to the right edge.
        Rectangle clock;
        IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero)
            notify = Native.FindWindowExW(tray, IntPtr.Zero, "ClockButton", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var cr))
            clock = cr.ToRectangle();
        else
            clock = Rectangle.FromLTRB(taskbar.Right - 8, taskbar.Top, taskbar.Right - 8, taskbar.Bottom);

        uint dpi = Native.GetDpiForWindow(tray);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        placement = new Placement(taskbar, clock, scale);
        return true;
    }

    private static List<IntPtr> Secondaries()
    {
        var found = new List<(IntPtr Hwnd, int Left)>();
        var sb = new StringBuilder(64);
        Native.EnumWindows((hwnd, _) =>
        {
            sb.Clear();
            if (Native.GetClassName(hwnd, sb, sb.Capacity) > 0 &&
                sb.ToString() == "Shell_SecondaryTrayWnd" &&
                Native.GetWindowRect(hwnd, out var r))
                found.Add((hwnd, r.Left));
            return true;
        }, IntPtr.Zero);
        return [.. found.OrderBy(f => f.Left).Select(f => f.Hwnd)];
    }
}
