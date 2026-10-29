using System;
using System.Runtime.InteropServices;

namespace Windmill;

internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_GETICON = 0x007F;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const uint GW_OWNER = 4;
    public const int WS_EX_APPWINDOW = 0x40000;
    public const int DWMWA_CLOAKED = 14;

    public const uint LLKHF_INJECTED = 0x10;

    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_ESCAPE = 0x1B;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vk);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attr, out int value, int size);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    static extern uint GetClassLong32(IntPtr hWnd, int index);

    public static IntPtr GetClassLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, index) : (IntPtr)GetClassLong32(hWnd, index);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>PrintWindow flag that also captures DirectX/GPU-rendered content (browsers, modern apps).</summary>
    public const uint PW_RENDERFULLCONTENT = 0x2;

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    public static string TextOf(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static MONITORINFO GetMonitor(IntPtr hMon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        return mi;
    }

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Scale factor for the monitor (1.0 = 96 dpi).</summary>
    public static double GetDpiForMonitor(IntPtr hMonitor) =>
        GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0 ? x / 96.0 : 1.0;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>
    /// Press and release an unassigned key. Windows only opens an app's menu bar when Alt is pressed and
    /// released with nothing in between, so this keeps Alt-as-trigger from stealing focus into the menu.
    /// </summary>
    public static void SendDummyKey()
    {
        const ushort VK_UNASSIGNED = 0xE8;
        const uint KEYEVENTF_KEYUP = 0x2;
        var down = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_UNASSIGNED } } };
        var up = down;
        up.u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    public static string ClassOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
