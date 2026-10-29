using System;

namespace Windmill;

internal static class WindowSnapper
{
    /// <summary>True for windows that make sense to snap (not the desktop, taskbar, or Windmill itself).</summary>
    public static bool IsSnappable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        return Native.ClassOf(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    public static void Snap(IntPtr hwnd, Native.RECT work, Zone zone, int gap)
    {
        if (!Native.IsWindow(hwnd)) return;

        if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        if (zone.Maximize)
        {
            // Move onto the target monitor first so it maximizes there.
            Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + 40, work.Top + 40, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
            return;
        }

        int inset = gap / 2;
        int x = work.Left + (int)Math.Round(zone.Rect.X * work.Width) + inset;
        int y = work.Top + (int)Math.Round(zone.Rect.Y * work.Height) + inset;
        int w = (int)Math.Round(zone.Rect.W * work.Width) - inset * 2;
        int h = (int)Math.Round(zone.Rect.H * work.Height) - inset * 2;

        // Two passes: the first can change the window's DPI when crossing monitors, which alters its frame size.
        for (int pass = 0; pass < 2; pass++)
        {
            Native.GetWindowRect(hwnd, out var outer);
            // Win10/11 windows have invisible resize borders; compensate so the *visible* edge lands on target.
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible,
                    System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
                visible = outer;

            int l = visible.Left - outer.Left, t = visible.Top - outer.Top;
            int r = outer.Right - visible.Right, b = outer.Bottom - visible.Bottom;
            Native.SetWindowPos(hwnd, IntPtr.Zero, x - l, y - t, w + l + r, h + t + b,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }
}
