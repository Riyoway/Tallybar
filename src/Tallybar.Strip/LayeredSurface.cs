using System.Drawing;

namespace Tallybar;

/// <summary>
/// Blits a 32bpp premultiplied ARGB bitmap onto a WS_EX_LAYERED window with per-pixel
/// alpha, so a window can be genuinely translucent (its own rounded, semi-transparent
/// background instead of an opaque client). Position is the window's screen top-left.
/// </summary>
internal static class LayeredSurface
{
    public static void Push(IntPtr hwnd, Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
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

            Native.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size,
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
}
