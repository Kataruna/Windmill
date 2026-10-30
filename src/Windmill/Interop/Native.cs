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
    public const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    public const int WM_POINTERACTIVATE = 0x024B, PA_NOACTIVATE = 3;
    public const int POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_PRIMARY = 0x2000;

    /// <summary>Mouse input that Windows made up from a touch or pen contact carries this in its extra info.</summary>
    public static bool FromTouchOrPen(IntPtr extraInfo) => ((long)extraInfo & 0xFFFFFF00) == 0xFF515700;
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
    public const int WS_EX_TOPMOST = 0x8;

    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

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
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    const uint CURSOR_SUPPRESSED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public uint flags; public IntPtr hCursor; public POINT pt; }

    [DllImport("user32.dll")]
    static extern bool GetCursorInfo(ref CURSORINFO info);

    /// <summary>
    /// Touch was the last input, so there's no cursor and GetCursorPos means nothing until the mouse moves. Without a
    /// touchscreen it can only be a pen, and a pen tablet keeps the cursor where the pen is.
    /// </summary>
    public static bool CursorSuppressed()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0 && GetCursorInfo(ref info) && (info.flags & CURSOR_SUPPRESSED) != 0;
    }

    public const int SM_SWAPBUTTON = 23, SM_CXDRAG = 68, SM_CYDRAG = 69, SM_MAXIMUMTOUCHES = 95;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>Like ShowWindow, but doesn't wait for the window's thread, so a hung app can't block us.</summary>
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);

    public const int SW_SHOWMINNOACTIVE = 7;

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

    public const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    /// <summary>Hold Win, tap each arrow in turn, release Win; one SendInput call so nothing can interleave.</summary>
    public static void SendWinArrows(ushort[] arrows)
    {
        const ushort VK_LWIN = 0x5B;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        static INPUT Key(ushort vk, uint flags) =>
            new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_EXTENDEDKEY | flags } } };

        var inputs = new System.Collections.Generic.List<INPUT> { Key(VK_LWIN, 0) };
        foreach (var a in arrows) { inputs.Add(Key(a, 0)); inputs.Add(Key(a, KEYEVENTF_KEYUP)); }
        inputs.Add(Key(VK_LWIN, KEYEVENTF_KEYUP));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Presses and releases one key, as if typed.</summary>
    public static void SendKey(ushort vk)
    {
        const uint KEYEVENTF_KEYUP = 0x2;
        var inputs = new[]
        {
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowArranged")]
    static extern bool IsWindowArrangedImpl(IntPtr hWnd);

    /// <summary>True when Windows itself has snapped the window.</summary>
    public static bool IsWindowArranged(IntPtr hWnd)
    {
        if (_noIsWindowArranged) return false;
        try { return IsWindowArrangedImpl(hWnd); }
        catch (EntryPointNotFoundException) { _noIsWindowArranged = true; return false; } // older Windows 10
    }

    static bool _noIsWindowArranged; // remembered so polling loops don't throw on every call

    /// <summary>
    /// False on Windows 10 builds without IsWindowArranged. Native snap can't tell whether a snap worked there,
    /// so it isn't attempted.
    /// </summary>
    public static bool CanDetectSnap { get; } =
        NativeLibrary.TryLoad("user32.dll", out var user32) && NativeLibrary.TryGetExport(user32, "IsWindowArranged", out _);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    /// <summary>Scale factor (1.0 = 96 dpi) of the monitor the window is on, which is what its pixel size follows.</summary>
    public static double DpiOf(IntPtr hWnd) => GetDpiForMonitor(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST));

    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTCAPTION = 2;
    public const int WM_CANCELMODE = 0x001F;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_DRAGMOVE = 0xF012; // SC_MOVE + HTCAPTION: move as if the title bar were dragged

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hWnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hMod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    public static string ClassOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    class VirtualDesktopManager { }

    static IVirtualDesktopManager? _desktops;
    static bool _noDesktops;

    /// <summary>True unless the window is known to be on another virtual desktop. Minimized windows count too.</summary>
    public static bool OnCurrentDesktop(IntPtr hWnd)
    {
        if (_noDesktops) return true;
        try
        {
            _desktops ??= (IVirtualDesktopManager)new VirtualDesktopManager();
            return _desktops.IsWindowOnCurrentVirtualDesktop(hWnd, out bool on) != 0 || on;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _noDesktops = true; // e.g. Explorer isn't running
            return true;
        }
    }
}
