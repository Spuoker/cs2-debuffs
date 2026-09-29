using System.Drawing;
using System.Runtime.InteropServices;

namespace DebuffRoulette;

/// <summary>
/// Shared layered-window machinery (WS_EX_LAYERED): show a bitmap with per-pixel alpha
/// (UpdateLayeredWindow) and raise it above the game. This used to be copy-pasted into 5 overlays
/// (panel, banner, shame, glass, dirt) - now it is a single helper.
/// </summary>
internal static class LayeredWindow
{
    private const int UlwAlpha = 0x2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private const uint SwpNoMove = 0x2, SwpNoSize = 0x1, SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);

    [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size32 { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref Point32 pptDst,
        ref Size32 psize, IntPtr hdcSrc, ref Point32 pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    /// <summary>Show a bitmap (per-pixel alpha) at screen position (x,y). Returns success, never throws.</summary>
    public static bool Push(IntPtr hwnd, Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);
            var size = new Size32 { Cx = bmp.Width, Cy = bmp.Height };
            var src = new Point32 { X = 0, Y = 0 };
            var dst = new Point32 { X = x, Y = y };
            var blend = new BlendFunction { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
            return UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, UlwAlpha);
        }
        catch { return false; }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
        }
    }

    /// <summary>
    /// Raise the window to the front. hard=true drops and re-applies TOPMOST (to jump above
    /// borderless CS2; the gap is sub-frame, so it does not flicker); hard=false does a soft HWND_TOP
    /// without clearing topmost.
    /// </summary>
    public static void RaiseTopmost(IntPtr hwnd, bool hard)
    {
        try
        {
            if (hard)
            {
                SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
            }
            else
            {
                SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
        catch { }
    }
}
