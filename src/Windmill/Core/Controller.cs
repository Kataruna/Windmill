using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    Settings _settings = new(); // read by Reload
    List<Layout> _layouts = new();
    int[][] _chord = Array.Empty<int[]>();   // one entry per required key; each lists its acceptable vk codes; empty when the snap menu is off
    int[][] _arrangeChord = Array.Empty<int[]>(); // empty when auto-arrange is turned off
    HashSet<int> _triggerVks = new();        // keys of both chords
    readonly HashSet<int> _held = new();
    State _state = State.Idle;
    Mode _mode;
    ArrangeView? _arrange;
    List<IntPtr>? _arrangeShown; // komorebi: every window listed for this monitor; the ones not picked get minimized
    Task<HashSet<long>?>? _komorebiProbe; // started when the auto-arrange shortcut goes down; see Komorebi.ProbeAsync
    bool _activating;     // auto-arrange is waiting for the komorebi probe before it opens
    CancellationTokenSource? _captures;   // background window snapshots for the arrange cards
    bool _clickSwallowed;
    int _wheel;         // scrolled so far towards the next layout
    const int WheelNotch = 120;
    bool _shortcutReleased; // toggle: every trigger key has been let go since the menu opened
    bool _blocked;      // set after a gesture ends or is aborted, until every trigger key is released
    int _layoutIndex;

    IntPtr _target;
    IntPtr _hMonitor;
    Native.MONITORINFO _monitor;
    Point _origin;      // menu centre (cursor when it opened), physical screen pixels
    Rect _panel;        // miniature zone map, physical screen pixels
    bool _moved;        // cursor has left the dead zone since the menu appeared
    Point? _touch;      // last finger/pen point on the menu; stands in for the cursor until the mouse moves
    Native.POINT _cursorAtTouch;

    // Keyboard aim in the snap menu: arrows or WASD, two at once for a diagonal, Up twice for full screen. Like a
    // touch, it stands in for the cursor until the mouse moves.
    (int X, int Y, bool Full)? _aim;  // X: -1 left, 1 right; Y: 1 up, -1 down; Full: double-tapped Up
    Native.POINT _cursorAtAim;
    readonly HashSet<int> _aimHeld = new();  // direction keys down now; a press with none down starts a new aim
    long _upTappedAt;                        // when Up alone was last pressed, for the double tap
    const int DoubleTapMs = 350;
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
        _pollTimer.Tick += (_, _) => Poll();
        // A menu's memory is nearly all native (WPF's drawing resources), freed only by finalizers, and with so little
        // managed garbage no collection comes along by itself. Once settled, ask for one, and off the hook's thread.
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (_state != State.Idle) return;
            Task.Run(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            });
        };
        _hook.Key = OnKey;
        _hook.Wheel = OnWheel;
        _hook.Click = OnClick;
        _overlay.Touch = OnTouch;
        _hook.MouseHookFailed = error =>
        {
            if (_warnedMouseHook) return; // retried on every open; say it once
            _warnedMouseHook = true;
            // Can arrive inside a hook callback, which must return quickly.
            _ui.BeginInvoke(() => Warning?.Invoke($"Windmill couldn't watch the mouse (error {error}), so scrolling and clicking won't work " +
                "in the menu. Use Tab to change layout and Enter or Esc to finish."));
        };
    }

    /// <summary>Something the user should know about that doesn't stop Windmill from running. Raised on the UI thread.</summary>
    public event Action<string>? Warning;
    bool _warnedMouseHook;

    public void Reload()
    {
        // The layouts and shortcuts below may no longer match an open menu (Alt+F4 on the settings window, say).
        if (_state != State.Idle) { _holdTimer.Stop(); Close(snapped: false); }
        _settings = Settings.Load();
        _layouts = Layouts.LoadAll(_settings);
        _layoutIndex = Math.Min(_layoutIndex, _layouts.Count - 1);
        _chord = _settings.SnapEnabled
            ? _settings.TriggerKeys.Select(ParseTrigger).ToArray()
            : Array.Empty<int[]>();
        _arrangeChord = _settings.ArrangeEnabled
            ? _settings.ArrangeKeys.Select(ParseTrigger).ToArray()
            : Array.Empty<int[]>();
        _triggerVks = _chord.Concat(_arrangeChord).SelectMany(k => k).ToHashSet();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoldDelayMs));
        RadialMetrics.Scale = Math.Clamp(_settings.RingSizePercent, 50, 150) / 100.0;
        DragRestore.Enabled = _settings.SnapStyle == SnapStyle.Native;
        _overlay.Blur = _settings.BlurBehindMenu;
        TrimSoon(); // also lets go of what startup or the settings window used
    }

    void TrimSoon()
    {
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    // Left/right variants share a name so "Alt" works on either side; "LAlt" and the like mean that side only.
    static int[] ParseTrigger(string name) => name.Trim().ToLowerInvariant() switch
    {
        "alt" => new[] { 0xA4, 0xA5 },
        "ctrl" or "control" => new[] { 0xA2, 0xA3 },
        "shift" => new[] { 0xA0, 0xA1 },
        "win" => new[] { 0x5B, 0x5C },
        "lalt" => new[] { 0xA4 },
        "ralt" => new[] { 0xA5 },
        "lctrl" => new[] { 0xA2 },
        "rctrl" => new[] { 0xA3 },
        "lshift" => new[] { 0xA0 },
        "rshift" => new[] { 0xA1 },
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
            // The hook runs before the key state updates, so a key that isn't down yet is a new press, not auto-repeat.
            bool fresh = (Native.GetAsyncKeyState(vk) & 0x8000) == 0;
            // Key-ups can be lost (lock screen, UAC), so forget keys that are no longer physically down.
            _held.RemoveWhere(k => k != vk && (Native.GetAsyncKeyState(k) & 0x8000) == 0);
            _held.Add(vk);
            // The lost key-up may be the one that would have re-armed the shortcut.
            if (_blocked && fresh && !_held.Any(k => k != vk && _triggerVks.Contains(k))) _blocked = false;
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
                        // Ask komorebi during the hold delay, on a worker: this is the hook callback.
                        if (m == Mode.Arrange)
                            _komorebiProbe = _settings.KomorebiIntegration ? Task.Run(Komorebi.ProbeAsync) : null;
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

        if (_state == State.Active && _mode == Mode.Snap && Direction(vk) is { } dir)
        {
            if (!down) _aimHeld.Remove(vk);
            else
            {
                bool fresh = _aimHeld.Count == 0;
                if (_aimHeld.Add(vk)) _ui.BeginInvoke(() => Aim(dir.X, dir.Y, fresh)); // not on auto-repeat
            }
            return true; // swallowed, so the window underneath doesn't get them
        }

        if (_state == State.Active && vk is Native.VK_TAB or Native.VK_ESCAPE or Native.VK_RETURN)
        {
            if (down)
            {
                if (vk == Native.VK_ESCAPE) Cancel();
                else if (vk == Native.VK_RETURN) Commit();
                else if (_mode == Mode.Snap) // arrange mode swallows Tab so Alt+Shift+Tab can't open the task switcher
                {
                    // Async: GetKeyState follows this thread's own input, and the keys are going to another window.
                    int step = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0 ? -1 : 1;
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
        // Touchpads and smooth wheels send small steps; one notch's worth changes the layout once.
        _wheel += delta;
        if (Math.Abs(_wheel) < WheelNotch) return true;
        int step = _wheel > 0 ? -1 : 1;
        _wheel = 0;
        _ui.BeginInvoke(() => CycleLayout(step));
        return true;
    }

    bool OnClick(bool down)
    {
        // Swallow the release only if we swallowed the press, so no app is left with a stuck button.
        if (!down)
        {
            bool ours = _clickSwallowed;
            _clickSwallowed = false;
            if (_state != State.Active) _ui.BeginInvoke(ReleaseMouse); // the press we swallowed has now been released
            return ours;
        }
        if (_state != State.Active) return false;
        if (_mode == Mode.Arrange) _ui.BeginInvoke(TogglePick);
        else if (Toggle) _ui.BeginInvoke(Commit); // click snaps to the highlighted zone, or closes if none
        else return false;
        _clickSwallowed = true;
        return true;
    }

    /// <summary>
    /// A finger or pen on the open menu points like the cursor does. Hold: releasing the shortcut snaps, as with the
    /// mouse. Toggle: lifting snaps to the zone under the finger (or closes). Auto-arrange: a tap picks a window.
    /// </summary>
    void OnTouch(TouchPhase phase, Point at)
    {
        if (_state != State.Active) return;
        _aim = null; // the keyboard aim would otherwise win over the finger
        _touch = at;
        Native.GetCursorPos(out _cursorAtTouch);
        Update();
        if (phase != TouchPhase.Up) return;
        if (_mode == Mode.Arrange) TogglePick();
        else if (Toggle) Commit();
    }

    /// <summary>Arrow keys and WASD as a direction (Y: 1 = up), or null for any other key.</summary>
    static (int X, int Y)? Direction(int vk) => vk switch
    {
        Native.VK_LEFT or 0x41 => (-1, 0),  // A
        Native.VK_RIGHT or 0x44 => (1, 0),  // D
        Native.VK_UP or 0x57 => (0, 1),     // W
        Native.VK_DOWN or 0x53 => (0, -1),  // S
        _ => null,
    };

    /// <summary>
    /// A direction key went down. Keys held together add up to a diagonal, and the aim stays when they're let go
    /// one by one; the next press after all are up starts over. Up pressed twice on its own aims at full screen.
    /// </summary>
    void Aim(int dx, int dy, bool fresh)
    {
        if (_state != State.Active) return;
        var (x, y, _) = fresh || _aim is not { } old ? (0, 0, false) : old;
        if (dx != 0) x = dx; // the axis' last key wins, so Left then Right aims right
        if (dy != 0) y = dy;

        bool full = false;
        long now = Environment.TickCount64;
        if (fresh && dx == 0 && dy == 1)
        {
            full = now - _upTappedAt <= DoubleTapMs;
            _upTappedAt = full ? 0 : now; // a third tap starts a new double tap
        }
        else _upTappedAt = 0;

        _aim = (x, y, full);
        Native.GetCursorPos(out _cursorAtAim);
        Update();
    }

    /// <summary>The point the keyboard aim stands for: on the ring (the inner arc for full screen) or the map's edge.</summary>
    Point AimPoint((int X, int Y, bool Full) aim)
    {
        if (!_layouts[_layoutIndex].Radial)
            return new Point(_panel.X + _panel.Width * (0.5 + 0.45 * aim.X), _panel.Y + _panel.Height * (0.5 - 0.45 * aim.Y));
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        double r = (aim.Full ? (RadialMetrics.Inner + RadialMetrics.Split) / 2 : (RadialMetrics.RingStart + RadialMetrics.Outer) / 2) * dpi;
        double a = aim.Full ? Math.PI / 2 : Math.Atan2(aim.Y, aim.X);
        return new Point(_origin.X + r * Math.Cos(a), _origin.Y - r * Math.Sin(a));
    }

    /// <summary>Where the user is pointing: the keyboard aim or the last touch, unless the mouse has moved since.</summary>
    Native.POINT Pointer()
    {
        Native.GetCursorPos(out var pt);
        if (_aim is { } aim && pt.X == _cursorAtAim.X && pt.Y == _cursorAtAim.Y)
        {
            var at = AimPoint(aim);
            return new Native.POINT { X = (int)Math.Round(at.X), Y = (int)Math.Round(at.Y) };
        }
        _aim = null;
        if (_touch is { } t && pt.X == _cursorAtTouch.X && pt.Y == _cursorAtTouch.Y)
            return new Native.POINT { X = (int)t.X, Y = (int)t.Y };
        _touch = null;
        return pt;
    }

    // ---- state transitions ----

    void Activate()
    {
        // A toggle tap and the hold timer can both get here. Only a second auto-arrange waits for the first; switching
        // to the snap menu meanwhile opens it, and the waiting auto-arrange then gives up.
        if (_state != State.Pending || (_activating && _mode == Mode.Arrange)) return;
        if (_mode == Mode.Arrange) { _ = ActivateArrangeAsync(); return; }

        var hwnd = Native.GetForegroundWindow();
        if (!WindowSnapper.IsSnappable(hwnd)) { _state = State.Idle; Block(); return; }

        _target = hwnd;
        Native.GetCursorPos(out var pt);
        var at = pt;
        Point? noCursor = null;
        // After touch there's no cursor and its position means nothing, so open on the window instead; that point
        // then stands in for the cursor, like a touch, until the mouse moves.
        if (Native.CursorSuppressed() && Native.GetWindowRect(hwnd, out var rect))
        {
            at = new Native.POINT { X = rect.Left + rect.Width / 2, Y = rect.Top + rect.Height / 2 };
            noCursor = new Point(at.X, at.Y);
            _cursorAtTouch = pt;
        }
        SetMonitor(Native.MonitorFromPoint(at, Native.MONITOR_DEFAULTTONEAREST));
        _origin = new Point(at.X, at.Y);
        _moved = false;
        PlacePanel();
        Begin(noCursor);
    }

    async Task ActivateArrangeAsync()
    {
        // Usually finished during the hold delay; a toggle tap can get here sooner. Awaited, not waited on, so the
        // keyboard hook on this thread keeps running.
        HashSet<long>? parked = null;
        if (_komorebiProbe is { } probe)
        {
            _activating = true;
            // Any failure just means no komorebi mode; it mustn't leave the gesture stuck half open.
            try { parked = await probe; }
            catch (Exception) { parked = null; }
            finally { _activating = false; }
            // Released, cancelled or switched to the snap menu meanwhile.
            if (_state != State.Pending || _mode != Mode.Arrange) return;
        }

        Native.GetCursorPos(out var pt);
        // After touch the cursor's position means nothing, so go by the window in use.
        var hMon = Native.CursorSuppressed()
            ? Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);

        // With komorebi: this monitor and virtual desktop only, minimized windows too, but not the windows komorebi
        // has put away on its other workspaces. List them before the overlay appears so it can't count itself.
        bool komorebi = parked != null;
        var cards = komorebi ? Arranger.FindWindows(hMon, parked) : Arranger.FindWindows();
        if (cards.Count == 0) { _state = State.Idle; Block(); return; }
        _arrangeShown = komorebi ? cards.Select(c => c.Hwnd).ToList() : null;

        SetMonitor(hMon);
        var mon = _monitor.rcMonitor;
        var work = _monitor.rcWork;
        double dpi = Native.GetDpiForMonitor(_hMonitor);
        _arrange = Arranger.Plan(cards, new Rect(work.Left - mon.Left, work.Top - mon.Top, work.Width, work.Height), dpi);
        _captures = new CancellationTokenSource();
        // About the size they're drawn at, in physical pixels; each stays cached while its window exists.
        Arranger.CaptureThumbnails(_arrange, (int)Math.Ceiling(Arranger.CardWidth * dpi), _ui, _captures.Token);
        Begin();
    }

    void Begin(Point? touch = null)
    {
        _selected = -1;
        _wheel = 0;
        _touch = touch;
        _aim = null;
        _aimHeld.Clear();
        _upTappedAt = 0;
        _state = State.Active;
        _shortcutReleased = !_held.Overlaps(_triggerVks); // already true when a toggle tap opened it
        Update();
        _hook.MouseEnabled = true;
        _overlay.Interactive = true;
        _pollTimer.Start();
        MaskModifiers();
    }

    /// <summary>Ignore the shortcut until every trigger key is released, if any are still down.</summary>
    void Block() => _blocked = _held.Overlaps(_triggerVks);

    void TogglePick()
    {
        if (_state != State.Active || _arrange is not { } ar) return;
        var pt = Pointer();
        int i = ar.HitTest(new Point(pt.X - _monitor.rcMonitor.Left, pt.Y - _monitor.rcMonitor.Top));
        if (i < 0) return;
        if (!ar.Picked.Remove(i) && ar.Picked.Count < Arranger.MaxWindows) ar.Picked.Add(i);
        ar.Revision++;
        Update();
    }

    void Commit()
    {
        if (_state != State.Active) return; // e.g. a queued click after Esc
        if (_mode == Mode.Arrange) { CommitArrange(); return; }

        var layout = _layouts[_layoutIndex];
        int index = _selected;
        var zone = index >= 0 ? layout.Zones[index] : null;
        var target = _target;
        var work = _monitor.rcWork;
        int gap = _settings.Gap;
        var style = _settings.SnapStyle;
        bool snapAssist = _settings.ShowSnapAssist;
        bool komorebi = _settings.KomorebiIntegration;
        Close(snapped: zone != null);
        // Moving another process's window can block, so keep it out of the hook callback.
        if (zone != null)
            _ui.BeginInvoke(async () =>
            {
                if (komorebi && await Task.Run(() => Komorebi.IsRunning() ? Komorebi.SnapAsync(target, layout, zone, index)
                                                                           : Task.FromResult(false)))
                    return;
                WindowSnapper.Snap(target, work, zone, gap, style, snapAssist);
            });
    }

    void CommitArrange()
    {
        var windows = _arrange is { } ar ? ar.Picked.Select(i => ar.Cards[i].Hwnd).ToList() : new List<IntPtr>();
        var work = _monitor.rcWork;
        int gap = _settings.Gap;
        bool remember = _settings.SnapStyle == SnapStyle.Native;
        var shown = _arrangeShown;
        Close(snapped: windows.Count > 0);
        if (windows.Count > 0)
            _ui.BeginInvoke(async () =>
            {
                // komorebi: only the picks stay on screen, in the order komorebi tiles them; any it doesn't tile are
                // arranged as usual.
                var rest = shown != null ? await Task.Run(() => Komorebi.ShowOnlyAsync(windows, shown)) : windows;
                if (rest.Count > 0) Arranger.Arrange(rest, work, gap, remember);
            });
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
        ReleaseMouse();
        Block(); // holding the trigger after a snap must not reopen the menu
        _captures?.Cancel();
        _captures = null;
        _arrange = null; // its icons and snapshots aren't needed between menus
        _arrangeShown = null;
        _overlay.Dismiss(snapped);
        TrimSoon();
    }

    /// <summary>Drops the mouse hook, unless it still has to swallow the release of a click it swallowed.</summary>
    void ReleaseMouse()
    {
        if (_state != State.Active && !_clickSwallowed) _hook.MouseEnabled = false;
    }

    void CycleLayout(int step)
    {
        if (_state != State.Active) return;
        _layoutIndex = (_layoutIndex + step + _layouts.Count) % _layouts.Count;
        _selected = -1;
        // Re-centre the menu under the cursor; a keyboard aim keeps the menu where it is and aims the same way.
        if (_aim == null)
        {
            var pt = Pointer();
            var mon = _monitor.rcMonitor; // the cursor may have wandered onto another monitor; the menu can't follow
            _origin = new Point(Math.Clamp(pt.X, mon.Left, mon.Right - 1), Math.Clamp(pt.Y, mon.Top, mon.Bottom - 1));
            _moved = false;
        }
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

    void Poll()
    {
        // A release can go unseen (UAC prompt, lock screen), which would leave the menu up with nothing held.
        if (_state == State.Active && !Toggle
            && !ActiveChord.All(variants => variants.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0)))
        {
            _held.RemoveWhere(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0);
            Close(snapped: false);
            return;
        }
        Update();
    }

    void Update()
    {
        if (_state != State.Active) return;
        var layout = _layouts[_layoutIndex];
        var pt = Pointer();

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
            ArrangeRevision = arrange?.Revision ?? 0,
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
        DragRestore.Enabled = false;
        _overlay.Close();
    }
}
