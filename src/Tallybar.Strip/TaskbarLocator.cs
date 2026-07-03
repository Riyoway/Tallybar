using System.Drawing;

namespace Tallybar;

/// <summary>
/// Finds the primary taskbar and the tray/clock zone so the strip can dock just left
/// of the clock. All rects are in physical pixels (process is Per-Monitor-V2 aware).
///
/// Currently targets the primary horizontal (bottom/top) taskbar; vertical taskbars
/// and secondary monitors (Shell_SecondaryTrayWnd) are not handled yet.
/// </summary>
internal static class TaskbarLocator
{
    public readonly record struct Placement(Rectangle TaskbarRect, Rectangle ClockRect, double Scale);

    public static bool TryGet(out Placement placement)
    {
        placement = default;

        IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var tb))
            return false;

        Rectangle taskbar = tb.ToRectangle();

        // TrayNotifyWnd (clock + system tray) exists on both Win10 and Win11 and is the
        // one stable right-side anchor. App icons are unreliable (Win11 centers them).
        Rectangle clock;
        IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var cr))
            clock = cr.ToRectangle();
        else
            clock = Rectangle.FromLTRB(taskbar.Right, taskbar.Top, taskbar.Right, taskbar.Bottom);

        uint dpi = Native.GetDpiForWindow(tray);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        placement = new Placement(taskbar, clock, scale);
        return true;
    }
}
