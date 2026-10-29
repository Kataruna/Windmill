using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Windmill;

/// <summary>Owns the hold-to-open state machine: Idle -> Pending (trigger down) -> Active (menu shown) -> commit/cancel.</summary>
internal sealed class Controller : IDisposable
{
    enum State { Idle, Pending, Active }
    enum Mode { Snap, Arrange }

    readonly InputHook _hook = new();
    readonly OverlayWindow _overlay = new();
    readonly DispatcherTimer _holdTimer = new();
    readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    Settings _settings = Settings.Load();
    List<Layout> _layouts = new();
    int[][] _chord = Array.Empty<int[]>();   // one entry per required key; each lists its acceptable vk codes
    int[][] _arrangeChord = Array.Empty<int[]>(); // empty when auto-arrange is turned off
    HashSet<int> _triggerVks = new();        // keys of both chords
    readonly HashSet<int> _held = new();
    State _state = State.Idle;
    Mode _mode;
    ArrangeView? _arrange;
    CancellationTokenSource? _captures;   // background window snapshots for the arrange cards
    bool _clickSwallowed;
    bool _shortcutReleased; // toggle: every trigger key has been let go since the menu opened
    bool _blocked;      // set after a gesture ends or is aborted, until every trigger key is released
    int _layoutIndex;

    IntPtr _target;
    IntPtr _hMonitor;
    Native.MONITORINFO _monitor;
    Point _origin;      // menu centre (cursor when it opened), physical screen pixels
    Rect _panel;        // miniature zone map, physical screen pixels
    bool _moved;        // cursor has left the dead zone since the menu appeared
    int _selected = -1;

    public bool Enabled { get; set; } = true;

    /// <summary>Temporarily ignore the trigger (e.g. while the settings window records a new shortcut).</summary>
    public bool Paused { get; set; }

    public string LayoutSummary
    {
        get
        {
            int fz = _layouts.Count(l => l.Name.StartsWith("FZ:", StringComparison.Ordinal));
            return fz > 0
                ? $"{_layouts.Count} layouts loaded, {fz} from PowerToys FancyZones"
                : $"{_layouts.Count} layouts loaded";
        }
    }

    public Controller()
    {
        Reload();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); Activate(); };
        _pollTimer.Tick += (_, _) => Update();
        _hook.Key = OnKey;
        _hook.Wheel = OnWheel;
        _hook.Click = OnClick;
    }

    public void Reload()
    {
        _settings = Settings.Load();
        _layouts = Layouts.LoadAll(_settings);
        _layoutIndex = Math.Min(_layoutIndex, _layouts.Count - 1);
        _chord = _settings.TriggerKeys.Select(ParseTrigger).ToArray();
        _arrangeChord = _settings.ArrangeEnabled
            ? _settings.ArrangeKeys.Select(ParseTrigger).ToArray()
            : Array.Empty<int[]>();
        _triggerVks = _chord.Concat(_arrangeChord).SelectMany(k => k).ToHashSet();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoldDelayMs));
        RadialMetrics.Scale = Math.Clamp(_settings.RingSizePercent, 50, 150) / 100.0;
    }

    // Left/right variants share a name so "Alt" works on either side.
    static int[] ParseTrigger(string name) => name.Trim().ToLowerInvariant() switch
    {
        "alt" => new[] { 0xA4, 0xA5 },
        "ctrl" or "control" => new[] { 0xA2, 0xA3 },
        "shift" => new[] { 0xA0, 0xA1 },
        "win" => new[] { 0x5B, 0x5C },
        _ => new[] { (int)Enum.Parse<System.Windows.Forms.Keys>(name, ignoreCase: true) },
    };

    /// <summary>Every required key is down (extra keys allowed, e.g. Shift for Shift+Tab while the menu is open).</summary>
    bool Holding(int[][] chord) => chord.Length > 0 && chord.All(variants => variants.Any(_held.Contains));

    /// <summary>Exactly this chord is down, so Alt doesn't also start the Alt+Shift gesture.</summary>
    bool Exact(int[][] chord) => Holding(chord) && _held.All(k => chord.Any(variants => variants.Contains(k)));

    int[][] ActiveChord => _mode == Mode.Arrange ? _arrangeChord : _chord;

    /// <summary>The current mode stays open after the shortcut is released, until a click, Enter or the shortcut again.</summary>
    bool Toggle =>
        (_mode == Mode.Arrange ? _settings.ArrangeActivation : _settings.SnapActivation) == ActivationStyle.Toggle;

    /// <summary>
    /// Inject a harmless key while Alt or Win is down, so releasing it doesn't open the app's menu bar or the Start menu.
    /// </summary>
    void MaskModifiers()
    {
        if (_held.Overlaps(AltWin)) Native.SendDummyKey();
    }

    static readonly int[] AltWin = { 0xA4, 0xA5, 0x5B, 0x5C };

    // ---- hook callbacks (must be fast) ----

    bool OnKey(int vk, bool down)
    {
        if (down)
        {
            // Key-ups can be lost (lock screen, UAC), so forget keys that are no longer physically down.
            _held.RemoveWhere(k => k != vk && (Native.GetAsyncKeyState(k) & 0x8000) == 0);
            _held.Add(vk);
        }
        else _held.Remove(vk);

        if (_triggerVks.Contains(vk))
        {
            if (_state == State.Active)
            {
                if (!Toggle)
                {
                    if (!down && !Holding(ActiveChord)) Commit();
                }
                // Toggle: the release that follows opening is ignored; pressing the shortcut again confirms.
                else if (!down && !_held.Overlaps(_triggerVks)) _shortcutReleased = true;
                else if (down && _shortcutReleased && Holding(ActiveChord))
                {
                    MaskModifiers();
                    Commit();
                }
            }
            else if (_state == State.Pending && !down && Toggle)
            {
                // Toggle: a quick tap opens the menu without waiting for the hold delay.
                _holdTimer.Stop();
                _ui.BeginInvoke(Activate);
            }
            else
            {
                Mode? mode = Exact(_chord) ? Mode.Snap : Exact(_arrangeChord) ? Mode.Arrange : null;
                if (mode is { } m && !_blocked && Enabled && !Paused)
                {
                    // Alt then Shift switches a pending Alt gesture to Alt+Shift; the hold delay restarts.
                    if (_state == State.Idle || _mode != m)
                    {
                        _mode = m;
                        _state = State.Pending;
                        _holdTimer.Stop();
                        _holdTimer.Start();
                        if (Toggle) MaskModifiers(); // a tap may release Alt/Win before the menu opens
                    }
                }
                else if (_state == State.Pending) { _holdTimer.Stop(); _state = State.Idle; }
            }
            if (!down && !_held.Overlaps(_triggerVks)) _blocked = false; // everything released: re-arm
            return false; // let the target app see the modifier so it can't get stuck
        }

        if (down && _held.Overlaps(_triggerVks) && _state != State.Active)
        {
            // Another key pressed with the trigger held: this is a normal shortcut, not a Windmill gesture.
            _holdTimer.Stop();
            _state = State.Idle;
            _blocked = true;
            return false;
        }

        if (_state == State.Active && vk is Native.VK_TAB or Native.VK_ESCAPE or Native.VK_RETURN)
        {
            if (down)
            {
                if (vk == Native.VK_ESCAPE) Cancel();
                else if (vk == Native.VK_RETURN) Commit();
                else if (_mode == Mode.Snap) // arrange mode swallows Tab so Alt+Shift+Tab can't open the task switcher
                {
                    int step = (Native.GetKeyState(Native.VK_SHIFT) & 0x8000) != 0 ? -1 : 1;
                    _ui.BeginInvoke(() => CycleLayout(step));
                }
            }
            return true;
        }
        return false;
    }

    bool OnWheel(int delta)
    {
        if (_state != State.Active) return false;
        if (_mode != Mode.Snap) return true; // keep the scroll from reaching the window underneath
        int step = delta > 0 ? -1 : 1;
        _ui.BeginInvoke(() => CycleLayout(step));
        return true;
    }

    bool OnClick(bool down)
    {
        // Swallow the release only if we swallowed the press, so no app is left with a stuck button.
        if (!down) { bool ours = _clickSwallowed; _clickSwallowed = false; return ours; }
        if (_state != State.Active) return false;
        if (_mode == Mode.Arrange) _ui.BeginInvoke(TogglePick);
        else if (Toggle) _ui.BeginInvoke(Commit); // click snaps to the highlighted zone, or closes if none
        else return false;
        _clickSwallowed = true;
        return true;
    }

    // ---- state transitions ----

    void Activate()
    {
        if (_state != State.Pending) return; // a toggle tap and the hold timer can both get here
        if (_mode == Mode.Arrange) { ActivateArrange(); return; }

        var hwnd = Native.GetForegroundWindow();
        if (!WindowSnapper.IsSnappable(hwnd)) { _state = State.Idle; Block(); return; }

        _target = hwnd;
        Native.GetCursorPos(out var pt);
        SetMonitor(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST));
        _origin = new Point(pt.X, pt.Y);
        _moved = false;
        PlacePanel();
        Begin();
    }

    void ActivateArrange()
    {
        // List windows before the overlay appears so it can't count itself.
        var cards = Arranger.FindWindows();
        if (cards.Count == 0) { _state = State.Idle; Block(); return; }

        Native.GetCursorPos(out var pt);
        SetMonitor(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST));
        var mon = _monitor.rcMonitor;
        var work = _monitor.rcWork;
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        _arrange = Arranger.Plan(cards, new Rect(work.Left - mon.Left, work.Top - mon.Top, work.Width, work.Height), dpi);
        _captures = new CancellationTokenSource();
        Arranger.CaptureThumbnails(_arrange.Cards, (int)(Arranger.CardWidth * 2 * dpi), _ui, _captures.Token);
        Begin();
    }

    void Begin()
    {
        _selected = -1;
        _state = State.Active;
        _shortcutReleased = !_held.Overlaps(_triggerVks); // already true when a toggle tap opened it
        Update();
        _pollTimer.Start();
        MaskModifiers();
    }

    /// <summary>Ignore the shortcut until every trigger key is released, if any are still down.</summary>
    void Block() => _blocked = _held.Overlaps(_triggerVks);

    void TogglePick()
    {
        if (_state != State.Active || _arrange is not { } ar) return;
        Native.GetCursorPos(out var pt);
        int i = ar.HitTest(new Point(pt.X - _monitor.rcMonitor.Left, pt.Y - _monitor.rcMonitor.Top));
        if (i < 0) return;
        if (!ar.Picked.Remove(i) && ar.Picked.Count < Arranger.MaxWindows) ar.Picked.Add(i);
        Update();
    }

    void Commit()
    {
        if (_state != State.Active) return; // e.g. a queued click after Esc
        if (_mode == Mode.Arrange) { CommitArrange(); return; }

        var zone = _selected >= 0 ? _layouts[_layoutIndex].Zones[_selected] : null;
        var target = _target;
        var work = _monitor.rcWork;
        int gap = _settings.Gap;
        Close(snapped: zone != null);
        // Moving another process's window can block, so keep it out of the hook callback.
        if (zone != null)
            _ui.BeginInvoke(() => WindowSnapper.Snap(target, work, zone, gap));
    }

    void CommitArrange()
    {
        var windows = _arrange is { } ar ? ar.Picked.Select(i => ar.Cards[i].Hwnd).ToList() : new List<IntPtr>();
        var work = _monitor.rcWork;
        int gap = _settings.Gap;
        Close(snapped: windows.Count > 0);
        if (windows.Count > 0)
            _ui.BeginInvoke(() => Arranger.Arrange(windows, work, gap));
    }

    void Cancel()
    {
        _state = State.Idle;
        Block();
        _ui.BeginInvoke(() => Close(snapped: false));
    }

    void Close(bool snapped)
    {
        _pollTimer.Stop();
        _state = State.Idle;
        Block(); // holding the trigger after a snap must not reopen the menu
        _captures?.Cancel();
        _captures = null;
        _overlay.Dismiss(snapped);
    }

    void CycleLayout(int step)
    {
        if (_state != State.Active) return;
        _layoutIndex = (_layoutIndex + step + _layouts.Count) % _layouts.Count;
        _selected = -1;
        // Re-centre the menu under the cursor.
        Native.GetCursorPos(out var pt);
        _origin = new Point(pt.X, pt.Y);
        _moved = false;
        PlacePanel();
        Update();
    }

    /// <summary>Centres the miniature map on the menu origin, pushed inward where it would leave the monitor.</summary>
    void PlacePanel()
    {
        var mon = _monitor.rcMonitor;
        var work = _monitor.rcWork;
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        double k = MiniMetrics.Size * dpi / Math.Max(work.Width, work.Height);
        double w = work.Width * k, h = work.Height * k;
        double m = MiniMetrics.Margin * dpi;
        double x = Math.Max(mon.Left + m, Math.Min(_origin.X - w / 2, mon.Right - m - w));
        double y = Math.Max(mon.Top + m, Math.Min(_origin.Y - h / 2, mon.Bottom - MiniMetrics.Below * dpi - h));
        _panel = new Rect(x, y, w, h);
    }

    // ---- per-frame update ----

    void SetMonitor(IntPtr hMon)
    {
        _hMonitor = hMon;
        _monitor = Native.GetMonitor(hMon);
        _overlay.PlaceOn(_monitor.rcMonitor);
    }

    void Update()
    {
        if (_state != State.Active) return;
        var layout = _layouts[_layoutIndex];
        Native.GetCursorPos(out var pt);

        // Every menu stays on the monitor it opened on.
        var mon = _monitor.rcMonitor;
        var work = _monitor.rcWork;
        var arrange = _mode == Mode.Arrange ? _arrange : null;
        _selected = arrange != null ? arrange.HitTest(new Point(pt.X - mon.Left, pt.Y - mon.Top))
            : layout.Radial ? PickRadial(layout, pt) : PickMini(layout, pt);

        _overlay.Render(new OverlayState
        {
            Layout = layout,
            Arrange = arrange,
            Toggle = Toggle,
            LayoutIndex = _layoutIndex,
            LayoutCount = _layouts.Count,
            Selected = _selected,
            Origin = new Point(_origin.X - mon.Left, _origin.Y - mon.Top),
            Cursor = new Point(pt.X - mon.Left, pt.Y - mon.Top),
            Panel = new Rect(_panel.X - mon.Left, _panel.Y - mon.Top, _panel.Width, _panel.Height),
            Work = new Rect(work.Left - mon.Left, work.Top - mon.Top, work.Width, work.Height),
        });
    }

    int PickRadial(Layout layout, Native.POINT pt)
    {
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        double dx = pt.X - _origin.X, dy = _origin.Y - pt.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // Nothing is selected until the cursor moves, so a quick hold-and-release does nothing.
        if (!_moved && dist < 12 * dpi) return -1;
        _moved = true;

        if (dist < RadialMetrics.Inner * dpi) return -1; // empty centre

        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        bool inInnerBand = dist < RadialMetrics.Split * dpi;
        int outer = -1;
        for (int i = 0; i < layout.Zones.Count; i++)
        {
            var z = layout.Zones[i];
            if (z.Angle is not double a) continue;
            double diff = Math.Abs(((angle - a + 540) % 360) - 180); // shortest angular distance, 0..180
            if (diff > 22.5) continue;
            if (z.Inner) { if (inInnerBand) return i; }
            else outer = i;
        }
        return outer;
    }

    int PickMini(Layout layout, Native.POINT pt)
    {
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        double dx = pt.X - _origin.X, dy = pt.Y - _origin.Y;
        if (!_moved && dx * dx + dy * dy < 144 * dpi * dpi) return -1; // same 12px dead zone as the radial
        _moved = true;

        // Overshooting the map still picks the zone at its edge, so a flick in a direction is enough.
        double nx = Math.Clamp((pt.X - _panel.X) / _panel.Width, 0, 0.9999);
        double ny = Math.Clamp((pt.Y - _panel.Y) / _panel.Height, 0, 0.9999);

        // Canvas layouts can overlap; the smallest zone under the cursor wins.
        int best = -1;
        for (int i = 0; i < layout.Zones.Count; i++)
        {
            var r = layout.Zones[i].Rect;
            if (r.Contains(nx, ny) && (best < 0 || r.Area < layout.Zones[best].Rect.Area)) best = i;
        }
        return best;
    }

    public void Dispose()
    {
        _hook.Dispose();
        _overlay.Close();
    }
}
