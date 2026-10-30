using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Windmill;

/// <summary>
/// Talks to the komorebi tiling window manager through komorebic. While komorebi tiles a window, moving it ourselves
/// just gets undone, so snaps become komorebi commands instead: directions swap the window with its neighbour, the
/// full-screen arc maximizes, and other layouts change the workspace layout.
/// Everything here starts processes and waits on komorebi, so call it off the UI thread: that thread also runs the
/// global keyboard hook, which every keypress on the system waits for.
/// </summary>
internal static class Komorebi
{
    public static bool IsRunning()
    {
        var found = Process.GetProcessesByName("komorebi");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    static string? _cli;

    /// <summary>komorebic.exe on PATH, next to the running komorebi.exe, or in the default install folder.</summary>
    static string? Cli()
    {
        if (_cli != null && File.Exists(_cli)) return _cli;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        foreach (var p in Process.GetProcessesByName("komorebi"))
        {
            try { if (Path.GetDirectoryName(p.MainModule?.FileName) is { } d) dirs.Insert(0, d); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "komorebi", "bin"));
        foreach (var d in dirs)
        {
            try
            {
                var path = Path.Combine(d.Trim('"'), "komorebic.exe");
                if (File.Exists(path)) return _cli = path;
            }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        return null;
    }

    /// <summary>Runs komorebic with the given arguments and returns its output, or null if it couldn't run or failed.</summary>
    static async Task<string?> RunAsync(params string[] args)
    {
        if (Cli() is not { } cli) return null;
        var info = new ProcessStartInfo(cli)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync(); // drain so a chatty error can't block it
            var exited = p.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(3000)) != exited)
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? await output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows komorebi currently tiles (not floating and not ignored), or null if its state couldn't be read.
    /// </summary>
    static async Task<HashSet<long>?> TiledAsync()
    {
        using var doc = await StateAsync();
        if (doc == null) return null;
        var result = new HashSet<long>();
        Collect(doc.RootElement, result);
        return result;
    }

    static async Task<JsonDocument?> StateAsync()
    {
        var json = await RunAsync("state");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The focused workspace's containers, each as the list of its windows, and the windows shown above them
    /// (maximized or monocle), which are out of the containers while raised.
    /// </summary>
    static List<List<long>> Containers(JsonDocument state, out int focused, out HashSet<long> raised)
    {
        var result = new List<List<long>>();
        focused = -1;
        raised = new HashSet<long>();
        try
        {
            var ws = Focused(Focused(state.RootElement.GetProperty("monitors")).GetProperty("workspaces"));
            foreach (var name in new[] { "maximized_window", "monocle_container" })
                if (ws.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.Object)
                    Collect(r, raised);
            var containers = ws.GetProperty("containers");
            focused = containers.GetProperty("focused").GetInt32();
            foreach (var c in containers.GetProperty("elements").EnumerateArray())
                result.Add(c.GetProperty("windows").GetProperty("elements").EnumerateArray()
                    .Select(w => w.GetProperty("hwnd").GetInt64()).ToList());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException
                                       or FormatException) { }
        return result;

        static JsonElement Focused(JsonElement ring) => ring.GetProperty("elements")[ring.GetProperty("focused").GetInt32()];
    }

    /// <summary>
    /// Polls komorebi's state until <paramref name="done"/> holds for the container index of <paramref name="hwnd"/>
    /// and the focused container index, or about a second passes. komorebic returns before komorebi has acted.
    /// </summary>
    static async Task<bool> WaitAsync(long hwnd, Func<int, int, bool> done, Action? retry = null)
    {
        for (int i = 0; i < 20; i++)
        {
            using (var state = await StateAsync())
            {
                if (state == null) return false;
                int index = Containers(state, out int focused, out var raised).FindIndex(c => c.Contains(hwnd));
                if (raised.Contains(hwnd)) return true; // the only window shown, so already focused
                if (index >= 0 && done(index, focused)) return true;
            }
            retry?.Invoke();
            await Task.Delay(30);
        }
        return false;
    }

    /// <summary>Focuses the window and waits until komorebi has noticed, since its commands act on the focused window.</summary>
    static Task<bool> FocusAsync(IntPtr hwnd)
    {
        Native.SetForegroundWindow(hwnd);
        // komorebi may still be re-focusing the window from a previous command, so keep asking.
        return WaitAsync(hwnd.ToInt64(), (index, focused) => index == focused,
            () => { if (Native.GetForegroundWindow() != hwnd) Native.SetForegroundWindow(hwnd); });
    }

    // Every "hwnd" in the state, except those under a floating-windows list unless asked for.
    static void Collect(JsonElement e, HashSet<long> into, bool floating = false)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    if (!floating && prop.Name.Contains("floating", StringComparison.OrdinalIgnoreCase)) continue;
                    if (prop.Name == "hwnd" && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var h))
                        into.Add(h);
                    else
                        Collect(prop.Value, into, floating);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, into, floating);
                break;
        }
    }

    /// <summary>
    /// Sends the snap to komorebi if it tiles <paramref name="hwnd"/>. Returns false when the window isn't tiled
    /// (floating, ignored, or komorebi can't be reached), so the caller can snap it the usual way.
    /// </summary>
    public static async Task<bool> SnapAsync(IntPtr hwnd, Layout layout, Zone zone, int zoneIndex)
    {
        bool focused;
        using (var state = await StateAsync())
        {
            if (state == null) return false;
            var tiled = new HashSet<long>();
            Collect(state.RootElement, tiled);
            long h = hwnd.ToInt64();
            if (!tiled.Contains(h)) return false;
            // Usually already focused: the menu acts on the foreground window. Saves a round trip to komorebi.
            int index = Containers(state, out int current, out var raised).FindIndex(c => c.Contains(h));
            focused = raised.Contains(h) || (index >= 0 && index == current);
        }
        // komorebi's commands act on its focused window; if this one can't be focused, a command would move another
        // window. Do nothing instead (handled: moving it ourselves would just be undone by komorebi).
        if (!focused && !await FocusAsync(hwnd)) return true;

        if (zone.Maximize) return await RunAsync("toggle-maximize") != null;

        if (layout.Radial)
        {
            foreach (var dir in Directions(zone))
                if (await RunAsync("move", dir) == null) return false;
            return true;
        }

        await RunAsync("change-layout", LayoutFor(layout));
        // The first zone of a layout is its main area; picking it promotes the window there.
        if (zoneIndex == 0) await RunAsync("promote");
        return true;
    }

    /// <summary>Directions to move toward a radial zone: one for a side, a horizontal and a vertical move for a corner.</summary>
    static IEnumerable<string> Directions(Zone zone)
    {
        var r = zone.Rect;
        double cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        if (cx < 0.45) yield return "left";
        else if (cx > 0.55) yield return "right";
        if (cy < 0.45) yield return "up";
        else if (cy > 0.55) yield return "down";
    }

    /// <summary>The komorebi layout closest to a Windmill or FancyZones layout.</summary>
    static string LayoutFor(Layout layout)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.002;
        var zones = layout.Zones.Select(z => z.Rect).ToList();
        if (zones.All(z => Near(z.Y, 0) && Near(z.H, 1))) return "columns";
        if (zones.All(z => Near(z.X, 0) && Near(z.W, 1))) return "rows";
        var main = zones[0];
        if (Near(main.H, 1) && zones.Skip(1).All(z => z.X >= main.X + main.W - 0.002))
            return Near(main.X, 0) ? "vertical-stack" : "right-main-vertical-stack";
        if (Near(main.W, 1) && zones.Skip(1).All(z => z.Y >= main.Y + main.H - 0.002)) return "horizontal-stack";
        return "grid";
    }

    /// <summary>
    /// Null if komorebi isn't running. Otherwise the windows it keeps on workspaces that aren't showing: depending on
    /// its hiding behaviour these look minimized, but they belong to another workspace and must stay there. Empty if
    /// komorebi doesn't answer within half a second.
    /// </summary>
    public static async Task<HashSet<long>?> ProbeAsync()
    {
        if (!IsRunning()) return null;
        var result = new HashSet<long>();
        var read = StateAsync();
        if (await Task.WhenAny(read, Task.Delay(500)) != read)
        {
            _ = read.ContinueWith(t => t.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            return result;
        }
        using var state = await read;
        if (state == null) return result;
        try
        {
            foreach (var monitor in state.RootElement.GetProperty("monitors").GetProperty("elements").EnumerateArray())
            {
                var workspaces = monitor.GetProperty("workspaces");
                int focused = workspaces.GetProperty("focused").GetInt32();
                int i = 0;
                foreach (var ws in workspaces.GetProperty("elements").EnumerateArray())
                    if (i++ != focused) Collect(ws, result, floating: true);
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
        return result;
    }

    /// <summary>
    /// Shows only the picked windows on this monitor: restores the minimized ones, minimizes every other window in
    /// <paramref name="others"/>, then orders the picks like <see cref="ArrangeAsync"/>. Returns picks komorebi doesn't
    /// tile, for the caller to arrange the usual way.
    /// </summary>
    public static async Task<List<IntPtr>> ShowOnlyAsync(List<IntPtr> picked, IEnumerable<IntPtr> others)
    {
        var restored = picked.Where(Native.IsIconic).ToList();
        foreach (var h in restored) Native.ShowWindowAsync(h, Native.SW_RESTORE);
        foreach (var h in others.Except(picked))
            if (!Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_SHOWMINNOACTIVE);

        // komorebi takes restored windows back into the layout when it sees them; give it a moment to. A window it
        // never tiles (floating or ignored) ends the wait at the limit, about a second.
        for (int i = 0; restored.Count > 0 && i < 10; i++)
        {
            if (await TiledAsync() is not { } tiled || restored.All(h => tiled.Contains(h.ToInt64()))) break;
            await Task.Delay(40);
        }
        return await ArrangeAsync(picked);
    }

    /// <summary>
    /// Orders tiled windows on their workspace so the first picked is the main window, the second next, and so on.
    /// Returns the windows komorebi doesn't tile, for the caller to arrange the usual way.
    /// </summary>
    public static async Task<List<IntPtr>> ArrangeAsync(List<IntPtr> windows)
    {
        if (await TiledAsync() is not { } tiled) return windows;
        var mine = windows.Where(h => tiled.Contains(h.ToInt64())).ToList();

        // Promoting moves a window to the front, so promote the last pick first and the first pick last.
        for (int i = mine.Count - 1; i >= 0; i--)
        {
            if (!await FocusAsync(mine[i])) continue;
            await RunAsync("promote");
            // Promoting focuses the window again once komorebi gets to it; wait so that can't steal the next focus.
            await WaitAsync(mine[i].ToInt64(), (index, _) => index == 0);
        }
        return windows.Except(mine).ToList();
    }
}
