using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace Windmill;

/// <summary>
/// Native: zones Windows can snap to itself (halves, quarters, no gap) use real Windows snap; every other zone is
/// moved and resized, and returns to its old size when dragged away. Classic: always just move and resize.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SnapStyle>))]
internal enum SnapStyle { Native, Classic }

internal static class WindowSnapper
{
    /// <summary>True for windows that make sense to snap (not the desktop, taskbar, or Windmill itself).</summary>
    public static bool IsSnappable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        return Native.ClassOf(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    // Per window, bumped by every snap of that window. A native snap still waiting or checking its result gives up
    // once a newer snap of the same window has started, so its fallback can't drag the window back to the old zone.
    // Snapping a different window doesn't interrupt it. UI thread only.
    static readonly Dictionary<IntPtr, int> _generation = new();

    static int NextGeneration(IntPtr hwnd)
    {
        foreach (var gone in new List<IntPtr>(_generation.Keys))
            if (!Native.IsWindow(gone)) _generation.Remove(gone);
        int g = _generation.GetValueOrDefault(hwnd) + 1;
        _generation[hwnd] = g;
        return g;
    }

    /// <param name="snapAssist">Leave Windows' Snap Assist open after a native snap.</param>
    public static void Snap(IntPtr hwnd, Native.RECT work, Zone zone, int gap, SnapStyle style, bool snapAssist)
    {
        int generation = NextGeneration(hwnd);
        if (style == SnapStyle.Native && gap == 0 && !zone.Maximize && ArrowsFor(zone.Rect) is { } arrows)
            _ = SnapNativeAsync(hwnd, work, zone, arrows, snapAssist, generation);
        else
            Place(hwnd, work, zone, gap, remember: style == SnapStyle.Native);
    }

    /// <summary>Win+Arrow presses that put a restored window into <paramref name="r"/>, or null if Windows has no such snap.</summary>
    public static ushort[]? ArrowsFor(NRect r)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.002;
        if (!Near(r.W, 0.5)) return null;
        ushort side = Near(r.X, 0) ? Native.VK_LEFT : Near(r.X, 0.5) ? Native.VK_RIGHT : (ushort)0;
        if (side == 0) return null;
        if (Near(r.Y, 0) && Near(r.H, 1)) return new[] { side };
        if (Near(r.H, 0.5) && Near(r.Y, 0)) return new[] { side, Native.VK_UP };
        if (Near(r.H, 0.5) && Near(r.Y, 0.5)) return new[] { side, Native.VK_DOWN };
        return null;
    }

    static async Task SnapNativeAsync(IntPtr hwnd, Native.RECT work, Zone zone, ushort[] arrows, bool snapAssist, int generation)
    {
        bool Superseded() => generation != _generation.GetValueOrDefault(hwnd);

        // Win+Ctrl/Shift/Alt+Arrow are different commands, so wait for the user to let go of every modifier
        // (in Toggle mode the shortcut is still down at commit). Async: the input hooks live on this thread.
        for (int i = 0; ModifierDown(); i++)
        {
            if (i > 200) { Place(hwnd, work, zone, 0, remember: true); return; } // ~3 s
            await Task.Delay(15);
            if (Superseded()) return;
        }
        if (!Native.IsWindow(hwnd)) return;

        // Start from a plain restored window: Win+Left on a left-snapped window would jump to the next monitor.
        if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd) || Native.IsWindowArranged(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            await Task.Delay(50); // let Windows finish leaving the snapped state before the next snap
            if (Superseded()) return;
        }

        // Win+Arrow snaps on the window's current monitor, so move it there first.
        var center = new Native.POINT { X = (work.Left + work.Right) / 2, Y = (work.Top + work.Bottom) / 2 };
        if (Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) != Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST))
            Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + 40, work.Top + 40, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        // Win+Arrow acts on the foreground window; if Windows refuses to bring ours forward, don't snap someone else's.
        Native.SetForegroundWindow(hwnd);
        if (Native.GetForegroundWindow() == hwnd)
        {
            // One arrow at a time: an Up sent while the first snap is still in progress maximizes instead.
            bool snapped = true;
            for (int a = 0; a < arrows.Length; a++)
            {
                // The user may have clicked another window while we waited; the arrow would act on that one.
                if (a > 0 && Native.GetForegroundWindow() != hwnd) { snapped = false; break; }
                var arrow = arrows[a];
                var start = VisibleRect(hwnd, out _);
                Native.SendWinArrows(new[] { arrow });
                for (int i = 0; i < 30 && !(Native.IsWindowArranged(hwnd) && !Same(VisibleRect(hwnd, out _), start)); i++)
                {
                    await Task.Delay(15);
                    if (Superseded()) return;
                }
                // Didn't snap: stop, since Win+Up/Down on an unsnapped window would maximize or minimize it.
                if (!Native.IsWindowArranged(hwnd)) { snapped = false; break; }

                // Snap Assist (the "pick a window for the other side" panel) grabs focus right after a snap, which
                // would swallow the next arrow. Taking focus back closes it. Left open after the last one if wanted.
                if (snapAssist && a == arrows.Length - 1) break;
                for (int i = 0; i < 20; i++) // ~300 ms
                {
                    if (Native.ClassOf(Native.GetForegroundWindow()) == "XamlExplorerHostIslandWindow")
                    {
                        Native.SetForegroundWindow(hwnd);
                        break;
                    }
                    await Task.Delay(15);
                    if (Superseded()) return;
                }
            }
            var target = TargetRect(work, zone, 0);
            for (int i = 0; snapped && i < 40; i++) // ~600 ms
            {
                await Task.Delay(15);
                if (Superseded()) return;
                var v = VisibleRect(hwnd, out _);
                if (Math.Abs(v.Left - target.Left) <= 8 && Math.Abs(v.Top - target.Top) <= 8 &&
                    Math.Abs(v.Right - target.Right) <= 8 && Math.Abs(v.Bottom - target.Bottom) <= 8)
                    return;
            }
        }
        // Snapping is off in Windows settings, the window is elevated, or it landed elsewhere: do it ourselves.
        Place(hwnd, work, zone, 0, remember: true);
    }

    internal static bool Same(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    static bool ModifierDown()
    {
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) // Shift, Ctrl, Alt, LWin, RWin
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    static Native.RECT TargetRect(Native.RECT work, Zone zone, int gap)
    {
        int inset = gap / 2;
        int x = work.Left + (int)Math.Round(zone.Rect.X * work.Width) + inset;
        int y = work.Top + (int)Math.Round(zone.Rect.Y * work.Height) + inset;
        int w = (int)Math.Round(zone.Rect.W * work.Width) - inset * 2;
        int h = (int)Math.Round(zone.Rect.H * work.Height) - inset * 2;
        return new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>The window's visible edges, without the invisible resize borders Win10/11 add around it.</summary>
    static Native.RECT VisibleRect(IntPtr hwnd, out Native.RECT outer)
    {
        Native.GetWindowRect(hwnd, out outer);
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible,
                Marshal.SizeOf<Native.RECT>()) != 0)
            visible = outer;
        return visible;
    }

    /// <summary>Moves and resizes the window into the zone. <paramref name="remember"/>: restore its size when dragged away.</summary>
    public static void Place(IntPtr hwnd, Native.RECT work, Zone zone, int gap, bool remember)
    {
        NextGeneration(hwnd); // any placement (auto-arrange too) supersedes a native snap of this window in progress
        if (!Native.IsWindow(hwnd)) return;

        if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd) || (remember && Native.IsWindowArranged(hwnd)))
            Native.ShowWindow(hwnd, Native.SW_RESTORE); // leaving Windows snap first means we remember the real size

        if (zone.Maximize)
        {
            // Move onto the target monitor first so it maximizes there.
            Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + 40, work.Top + 40, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
            return;
        }

        var t = TargetRect(work, zone, gap);
        Native.GetWindowRect(hwnd, out var before);

        // Two passes: the first can change the window's DPI when crossing monitors, which alters its frame size.
        for (int pass = 0; pass < 2; pass++)
        {
            var visible = VisibleRect(hwnd, out var outer);
            // Compensate for the invisible borders so the *visible* edge lands on target.
            int l = visible.Left - outer.Left, tp = visible.Top - outer.Top;
            int r = outer.Right - visible.Right, b = outer.Bottom - visible.Bottom;
            Native.SetWindowPos(hwnd, IntPtr.Zero, t.Left - l, t.Top - tp, t.Width + l + r, t.Height + tp + b,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        if (remember)
        {
            Native.GetWindowRect(hwnd, out var after);
            DragRestore.Remember(hwnd, before, after);
        }
    }
}

/// <summary>
/// Gives windows Windmill resized the same feel as Windows snap: start dragging one by its title bar and it
/// returns to the size it had before it was snapped.
/// </summary>
internal static class DragRestore
{
    // hwnd -> size before the first snap, and the rect Windmill left it at. UI thread only.
    static readonly Dictionary<IntPtr, (int W, int H, Native.RECT Applied)> Snapped = new();
    static readonly Native.WinEventProc Proc = OnMoveSizeStart; // kept alive while hooked
    static IntPtr _hook;

    public static bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (value) _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZESTART,
                IntPtr.Zero, Proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
            else { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; Snapped.Clear(); }
        }
    }

    public static void Remember(IntPtr hwnd, Native.RECT before, Native.RECT after)
    {
        if (!Enabled) return;
        foreach (var gone in new List<IntPtr>(Snapped.Keys))
            if (!Native.IsWindow(gone)) Snapped.Remove(gone);

        // Moving from one zone to another keeps the original size, as long as nobody resized it in between.
        var size = Snapped.TryGetValue(hwnd, out var old) && WindowSnapper.Same(old.Applied, before)
            ? (old.W, old.H) : (before.Width, before.Height);
        Snapped[hwnd] = (size.Item1, size.Item2, after);
    }

    static bool LeftButtonDown() => (Native.GetAsyncKeyState(0x01) & 0x8000) != 0; // VK_LBUTTON

    static bool SizeUnchanged(IntPtr hwnd, Native.RECT applied, out Native.RECT cur)
    {
        // Size only: by the time we look, the drag may already have moved the window.
        Native.GetWindowRect(hwnd, out cur);
        return cur.Width == applied.Width && cur.Height == applied.Height && !Native.IsZoomed(hwnd);
    }

    static void OnMoveSizeStart(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || !Snapped.TryGetValue(hwnd, out var s)) return;

        // Resized, maximized or re-snapped by Windows since: its current size is what the user wants now.
        if (!SizeUnchanged(hwnd, s.Applied, out _)) { Snapped.Remove(hwnd); return; }

        // This event also fires for resizing from an edge; only a title-bar drag restores.
        Native.GetCursorPos(out var down);
        IntPtr lp = (IntPtr)((down.Y << 16) | (down.X & 0xFFFF));
        if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lp, Native.SMTO_ABORTIFHUNG, 50, out var hit) == IntPtr.Zero
            || (int)hit != Native.HTCAPTION)
        {
            Snapped.Remove(hwnd);
            return;
        }

        // The move loop starts on the button press itself, so a plain click on the title bar lands here too.
        // Keyboard moves (Alt+Space, M) have no button down and leave the window alone.
        _ = RestoreOnceDraggedAsync(hwnd, down);
    }

    /// <summary>Restores the window's old size once the cursor moves past the drag threshold; a click does nothing.</summary>
    static async Task RestoreOnceDraggedAsync(IntPtr hwnd, Native.POINT down)
    {
        int dx = Native.GetSystemMetrics(Native.SM_CXDRAG), dy = Native.GetSystemMetrics(Native.SM_CYDRAG);
        while (LeftButtonDown())
        {
            Native.GetCursorPos(out var pt);
            if (Math.Abs(pt.X - down.X) > dx || Math.Abs(pt.Y - down.Y) > dy)
            {
                // Remove first: the fresh drag below raises another move-size start.
                if (!Snapped.Remove(hwnd, out var s) || !SizeUnchanged(hwnd, s.Applied, out var cur)) return;

                // The running drag loop would size the window straight back, so end it, resize, and start a fresh
                // drag (the button is still down, so it carries on seamlessly).
                Native.SendMessageTimeout(hwnd, Native.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 50, out _);
                // Keep the cursor at the same fraction across the title bar, like Windows does.
                int x = pt.X - (int)((long)(pt.X - cur.Left) * s.W / Math.Max(1, cur.Width));
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, cur.Top, s.W, s.H, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                if (LeftButtonDown())
                    Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_DRAGMOVE, IntPtr.Zero);
                return;
            }
            await Task.Delay(15);
            if (!Native.IsWindow(hwnd)) return;
        }
        // Released without moving: just a click. The window stays in its zone and keeps its old size on record.
    }
}
