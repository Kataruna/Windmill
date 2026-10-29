using System;
using System.Runtime.InteropServices;

namespace Windmill;

/// <summary>
/// Global low-level keyboard hook, plus a mouse hook only while it's needed (see <see cref="MouseEnabled"/>).
/// Callbacks must return quickly or Windows drops the hook.
/// </summary>
internal sealed class InputHook : IDisposable
{
    readonly Native.HookProc _keyboardProc;
    readonly Native.HookProc _mouseProc;
    IntPtr _keyboardHook, _mouseHook;

    /// <summary>(vkCode, isDown) -> true to swallow the key.</summary>
    public Func<int, bool, bool>? Key;

    /// <summary>Wheel delta (positive = scrolled up) -> true to swallow the event.</summary>
    public Func<int, bool>? Wheel;

    /// <summary>Left button (true = pressed) -> true to swallow the event.</summary>
    public Func<bool, bool>? Click;

    public InputHook()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, Native.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new InvalidOperationException("Could not install input hooks (error " + Marshal.GetLastWin32Error() + ").");
    }

    /// <summary>
    /// Every mouse move on the system waits for a low-level mouse hook, so it's only installed while a menu is
    /// open; otherwise a busy UI thread here would make the cursor stutter everywhere.
    /// </summary>
    public bool MouseEnabled
    {
        get => _mouseHook != IntPtr.Zero;
        set
        {
            if (value == MouseEnabled) return;
            if (value) _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
            else { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }
    }

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & Native.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if ((down || up) && Key?.Invoke((int)data.vkCode, down) == true)
                    return (IntPtr)1;
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == Native.WM_MOUSEWHEEL)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int delta = (short)(data.mouseData >> 16);
            if (Wheel?.Invoke(delta) == true)
                return (IntPtr)1;
        }
        else if (code >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)
        {
            if (Click?.Invoke((int)wParam == Native.WM_LBUTTONDOWN) == true)
                return (IntPtr)1;
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
