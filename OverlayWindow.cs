using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Windmill;

/// <summary>Everything the overlay needs to draw one frame. All coordinates are physical pixels relative to the monitor's top-left.</summary>
internal sealed class OverlayState
{
    public Layout Layout = null!;
    public ArrangeView? Arrange;    // set in auto-arrange mode, which draws window cards instead of the layout
    public bool Toggle;             // menu stays open after the shortcut is released (changes the hints)
    public int LayoutIndex, LayoutCount;
    public int Selected = -1;
    public Point Origin;
    public Point Cursor;
    public Rect Panel;
    public Rect Work;
}

/// <summary>Borderless, click-through, never-activated window that covers one monitor.</summary>
internal sealed class OverlayWindow : Window
{
    readonly Surface _surface = new();
    readonly IntPtr _hwnd;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _surface;
        _surface.Dismissed = Hide;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    /// <summary>Frosted backdrop behind the ring and panel (Settings.BlurBehindMenu).</summary>
    public bool Blur { get => _surface.Blur; set => _surface.Blur = value; }

    public void PlaceOn(Native.RECT monitor)
    {
        _surface.ScreenOffset = new Point(monitor.Left, monitor.Top);
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            Native.SWP_NOACTIVATE);
    }

    public void Render(OverlayState state) => _surface.Present(state);

    /// <summary>Plays the closing animation, then hides. <paramref name="snapped"/> picks a "confirm" or "cancel" exit.</summary>
    public void Dismiss(bool snapped)
    {
        if (IsVisible) _surface.Dismiss(snapped);
    }

    /// <summary>
    /// Draws the menu immediately (no retained visuals). Animated values are stepped once per
    /// composition frame while the overlay is shown, and the frame is redrawn from them.
    /// </summary>
    sealed class Surface : FrameworkElement
    {
        const double FadeMs = 160, OpenMs = 300, StaggerMs = 22, CloseMs = 150, SettledMs = 2000;
        const double HoverRate = 22, FollowRate = 18; // exponential approach speeds, 1/s

        public Action? Dismissed;
        public bool Blur;
        public Point ScreenOffset; // monitor's top-left on the virtual screen, for capturing what's behind the menu

        // Blurred snapshot of the screen under the menu, taken when it opens or moves.
        ImageSource? _backdrop;
        Rect _backdropRect;
        Point _backdropOrigin;

        OverlayState? _state;
        bool _shown, _closing, _snapped;
        bool _fadeIn;             // false when only the layout changed, so the whole overlay doesn't blink
        double _elapsed;          // ms since the menu (or the current layout) appeared
        double _close;            // 0 -> 1 while dismissing
        double[] _hover = Array.Empty<double>();
        Rect _preview;
        double _previewAlpha;
        double _needleAngle, _needleAlpha;

        // Auto-arrange: how picked each card looks, its last order number, and the preview slots.
        double[] _pick = Array.Empty<double>();
        double[] _thumb = Array.Empty<double>();   // snapshot fade-in per card
        int[] _badge = Array.Empty<int>();
        readonly Rect[] _slot = new Rect[Arranger.MaxWindows];
        readonly double[] _slotAlpha = new double[Arranger.MaxWindows];

        readonly Stopwatch _clock = new();

        public Surface() => IsHitTestVisible = false;

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

        static readonly Color Fallback = Color.FromRgb(0x3B, 0x82, 0xF6);

        // Respect "Show animations in Windows" (Settings > Accessibility > Visual effects).
        static bool Motion => SystemParameters.ClientAreaAnimation;

        public void Present(OverlayState st)
        {
            bool fresh = !_shown || _closing || (_state != null && Content(st) != Content(_state));
            if (!_shown || _closing)
            {
                _shown = true;
                _closing = false;
                _close = 0;
                _fadeIn = true;
                _elapsed = 0;
                _needleAlpha = 0;
                ResetItems(st);
                StartTicking();
            }
            else if (_state != null && Content(st) != Content(_state))
            {
                _fadeIn = false;
                _elapsed = 0;
                ResetItems(st);
            }
            if (!Blur || st.Arrange != null) _backdrop = null;
            else if (fresh || st.Origin != _backdropOrigin) CaptureBackdrop(st);
            _state = st;
            InvalidateVisual();
        }

        /// <summary>
        /// Windows can only blur a whole rectangular window, so instead snapshot the screen behind the menu once,
        /// blur it, and draw it clipped to the menu's shape. Our own overlay is layered, so the plain screen copy skips it.
        /// </summary>
        void CaptureBackdrop(OverlayState st)
        {
            _backdropOrigin = st.Origin;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Rect area;
            double chipsTop; // the name and hint chips sit centred below the menu
            if (st.Layout.Radial)
            {
                double r = RadialMetrics.Outer * s * 1.15 + 24 * s; // room for the open/close overshoot and blur edge
                area = new Rect(st.Origin.X - r, st.Origin.Y - r, 2 * r, 2 * r);
                chipsTop = st.Origin.Y + RadialMetrics.Outer * s;
            }
            else
            {
                area = st.Panel;
                area.Inflate(8 * s, 8 * s);
                chipsTop = area.Bottom;
                area.Transform(new ScaleTransform(1.15, 1.15, st.Origin.X, st.Origin.Y).Value);
                area.Inflate(24 * s, 24 * s);
            }
            area.Union(new Rect(st.Origin.X - 320 * s, chipsTop, 640 * s, 110 * s));
            area.Intersect(new Rect(0, 0, ActualWidth * s, ActualHeight * s));
            if (area.IsEmpty || area.Width < 1 || area.Height < 1) { _backdrop = null; return; }

            const int Down = 4; // blur a quarter-size copy: much cheaper, and scaling it back up softens it further
            int w = (int)area.Width, h = (int)area.Height;
            try
            {
                using var shot = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(shot))
                    g.CopyFromScreen((int)(ScreenOffset.X + area.X), (int)(ScreenOffset.Y + area.Y), 0, 0, new System.Drawing.Size(w, h));
                int sw = Math.Max(1, w / Down), sh = Math.Max(1, h / Down);
                using var small = new System.Drawing.Bitmap(shot, sw, sh);
                var data = small.LockBits(new System.Drawing.Rectangle(0, 0, sw, sh),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                BitmapSource src;
                try { src = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * sh, data.Stride); }
                finally { small.UnlockBits(data); }

                var img = new System.Windows.Controls.Image { Source = src, Effect = new BlurEffect { Radius = 8 } };
                img.Measure(new Size(sw, sh));
                img.Arrange(new Rect(0, 0, sw, sh));
                var blurred = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                blurred.Render(img);
                blurred.Freeze();
                _backdrop = blurred;
                _backdropRect = area;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                _backdrop = null; // e.g. the secure desktop is up; just draw without blur
            }
        }

        /// <summary>Draws the blurred snapshot inside <paramref name="clip"/> (unscaled, so the background doesn't shift).</summary>
        void DrawBackdrop(DrawingContext dc, Geometry clip)
        {
            if (_backdrop == null) return;
            dc.PushClip(clip);
            dc.DrawImage(_backdrop, _backdropRect);
            dc.Pop();
        }

        /// <summary>What the menu is showing; a change re-runs the entrance cascade.</summary>
        static object Content(OverlayState st) => (object?)st.Arrange ?? st.Layout;

        void ResetItems(OverlayState st)
        {
            int n = st.Arrange?.Cards.Count ?? st.Layout.Zones.Count;
            _hover = new double[n];
            _pick = new double[n];
            _thumb = new double[n];
            _badge = new int[n];
            _previewAlpha = 0;
            Array.Clear(_slotAlpha);
        }

        public void Dismiss(bool snapped)
        {
            if (!_shown) { Dismissed?.Invoke(); return; }
            if (_closing) return;
            _closing = true;
            _snapped = snapped;
            _close = 0;
        }

        void StartTicking()
        {
            _clock.Restart();
            CompositionTarget.Rendering -= OnFrame;
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object? sender, EventArgs e)
        {
            double dt = Math.Min(_clock.Elapsed.TotalMilliseconds, 50); // don't jump after a stall
            _clock.Restart();
            Step(Motion ? dt : SettledMs);

            if (_closing && _close >= 1)
            {
                _shown = _closing = false;
                CompositionTarget.Rendering -= OnFrame;
                Dismissed?.Invoke();
                return;
            }
            InvalidateVisual();
        }

        void Step(double dt)
        {
            if (_state is not { } st) return;
            _elapsed = Math.Min(_elapsed + dt, SettledMs);
            if (_closing) _close = Math.Min(1, _close + dt / CloseMs);

            double hk = Approach(dt, HoverRate), fk = Approach(dt, FollowRate);
            for (int i = 0; i < _hover.Length; i++)
                _hover[i] += ((i == st.Selected ? 1 : 0) - _hover[i]) * hk;

            if (st.Arrange is { } ar)
            {
                for (int i = 0; i < _pick.Length; i++)
                {
                    int order = ar.Picked.IndexOf(i);
                    if (order >= 0) _badge[i] = order + 1; // kept while un-picking so the badge can fade out
                    _pick[i] += ((order >= 0 ? 1 : 0) - _pick[i]) * hk;
                    if (ar.Cards[i].Thumb != null) _thumb[i] += (1 - _thumb[i]) * fk;
                }

                // Preview slots glide to their new shape as windows are added or removed.
                var slots = Arranger.Slots(ar.Picked.Count, ar.Portrait);
                for (int i = 0; i < _slot.Length; i++)
                {
                    if (i < slots.Count)
                    {
                        var target = ToScreen(ar.Map, slots[i]);
                        _slot[i] = _slotAlpha[i] < 0.05 ? target : LerpRect(_slot[i], target, fk);
                        _slotAlpha[i] += (1 - _slotAlpha[i]) * fk;
                    }
                    else _slotAlpha[i] -= _slotAlpha[i] * fk;
                }
                return;
            }

            if (st.Selected >= 0 && st.Selected < st.Layout.Zones.Count)
            {
                var target = ToScreen(st.Work, st.Layout.Zones[st.Selected].Rect);
                // A preview that has faded out appears in place; a visible one glides to the new zone.
                _preview = _previewAlpha < 0.05 ? target : LerpRect(_preview, target, fk);
                _previewAlpha += (1 - _previewAlpha) * fk;
            }
            else _previewAlpha -= _previewAlpha * fk;

            if (st.Layout.Radial)
            {
                double dx = st.Cursor.X - st.Origin.X, dy = st.Origin.Y - st.Cursor.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                double delta = ((angle - _needleAngle + 540) % 360) - 180; // turn the short way round
                _needleAngle = _needleAlpha < 0.05 ? angle : _needleAngle + delta * Approach(dt, 30);
                double want = Math.Clamp((dist - 10 * s) / (20 * s), 0, 1);
                _needleAlpha += (want - _needleAlpha) * fk;
            }
        }

        /// <summary>Fraction of the remaining distance to cover this frame, independent of frame rate.</summary>
        static double Approach(double dtMs, double rate) => 1 - Math.Exp(-rate * dtMs / 1000);

        // ---- drawing ----

        /// <summary>Overall opacity: fade in on open, fade out on dismiss.</summary>
        double Alpha =>
            (_fadeIn ? EaseOutCubic(Clamp01(_elapsed / FadeMs)) : 1) * (1 - EaseOutCubic(_close));

        /// <summary>Progress (0..1) of the n-th item's entrance; items cascade in one after another.</summary>
        double Entrance(int n) => Clamp01((_elapsed - n * StaggerMs) / (OpenMs - 60));

        /// <summary>Menu scales up from under the cursor on open; on dismiss it swells (snap) or shrinks (cancel).</summary>
        double MenuScale =>
            (_fadeIn ? Lerp(0.82, 1, EaseOutBack(Clamp01(_elapsed / OpenMs))) : 1) *
            Lerp(1, _snapped ? 1.06 : 0.9, EaseOutCubic(_close));

        protected override void OnRender(DrawingContext dc)
        {
            if (_state is not { } st || !_shown) return;

            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            dc.PushTransform(new ScaleTransform(1 / s, 1 / s)); // draw in physical pixels
            dc.PushOpacity(Alpha);

            var accent = SystemParameters.WindowGlassColor;
            if (accent.A == 0) accent = Fallback;
            accent.A = 255;

            if (st.Arrange is { } ar) DrawArrange(dc, st, ar, s, accent);
            else if (st.Layout.Radial) DrawRadial(dc, st, s, accent);
            else DrawMini(dc, st, s, accent);

            dc.Pop();
            dc.Pop();
        }

        void DrawPreview(DrawingContext dc, double s, Color accent)
        {
            if (_previewAlpha < 0.01) return;
            var r = _preview;
            r.Inflate(-14 * s * (1 - _previewAlpha), -14 * s * (1 - _previewAlpha)); // grows into place
            dc.PushOpacity(_previewAlpha);
            DrawZoneRect(dc, r, s, Brush(WithAlpha(accent, 90)), new Pen(Brush(accent), 2 * s), 10 * s);
            dc.Pop();
        }

        void DrawRadial(DrawingContext dc, OverlayState st, double s, Color accent)
        {
            // Preview of where the window will land, behind the ring.
            DrawPreview(dc, s, accent);

            double inner = RadialMetrics.Inner * s, split = RadialMetrics.Split * s;
            double ring = RadialMetrics.RingStart * s, outer = RadialMetrics.Outer * s;
            var o = st.Origin;

            DrawBackdrop(dc, new EllipseGeometry(o, outer * MenuScale, outer * MenuScale));
            dc.PushTransform(new ScaleTransform(MenuScale, MenuScale, o.X, o.Y));

            // Soft shadow so the ring reads on any wallpaper.
            var shadow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new(Color.FromArgb(110, 0, 0, 0), 0.0),
                    new(Color.FromArgb(80, 0, 0, 0), 0.72),
                    new(Color.FromArgb(0, 0, 0, 0), 1.0),
                },
            };
            dc.DrawEllipse(shadow, null, o, outer + 36 * s, outer + 36 * s);

            var bg = Color.FromArgb(PanelAlpha, 28, 28, 34);
            var iconIdle = Color.FromArgb(70, 255, 255, 255);
            var lineIdle = Color.FromArgb(150, 255, 255, 255);
            var edge = new Pen(Brush(Color.FromArgb(28, 255, 255, 255)), 1 * s);

            // The centre is intentionally empty: a faint ring marks the dead zone, a needle shows where the cursor points.
            dc.DrawEllipse(null, new Pen(Brush(Color.FromArgb(70, 255, 255, 255)), 1.5 * s), o, inner - 4 * s, inner - 4 * s);
            dc.DrawEllipse(Brush(Color.FromArgb((byte)(140 * (1 - _needleAlpha)), 255, 255, 255)), null, o, 3 * s, 3 * s);
            if (_needleAlpha > 0.01)
            {
                // Arc stroked with round caps; the caps add a little length, so the arc itself is a bit shorter.
                double nr = inner - 4 * s;
                var needle = new StreamGeometry();
                using (var ctx = needle.Open())
                {
                    ctx.BeginFigure(Polar(o, nr, _needleAngle - 15), false, false);
                    ctx.ArcTo(Polar(o, nr, _needleAngle + 15), new Size(nr, nr), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                needle.Freeze();
                dc.DrawGeometry(null, new Pen(Brush(WithAlpha(accent, (byte)(230 * _needleAlpha))), 6 * s)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, needle);
            }

            int n = 0;
            for (int i = 0; i < st.Layout.Zones.Count; i++)
            {
                var zone = st.Layout.Zones[i];
                if (zone.Angle is not double a) continue;
                double h = i < _hover.Length ? _hover[i] : 0;

                // Each wedge flies out from the centre in turn, then pops outward while hovered.
                double p = Entrance(n++);
                double grow = Lerp(0.55, 1, EaseOutBack(p));
                double pop = (zone.Inner ? 3 : 7) * s * h;
                double r0 = (zone.Inner ? inner : ring) * grow + pop;
                double r1 = (zone.Inner ? split : outer) * grow + pop;
                double half = 22.5 - 1.5 + 1.2 * h; // gap between wedges closes up slightly when selected

                dc.PushOpacity(EaseOutCubic(p));
                if (h > 0.01) // accent glow behind the selected wedge
                    dc.DrawGeometry(Brush(WithAlpha(accent, (byte)(70 * h))), null,
                        Wedge(o, r0 - 4 * s, r1 + 6 * s, a - half - 2, a + half + 2));
                dc.DrawGeometry(Brush(Mix(bg, accent, h)), edge, Wedge(o, r0, r1, a - half, a + half));

                double icon = (zone.Inner ? 0.6 : 1) * (1 + 0.12 * h) * grow * RadialMetrics.Scale;
                DrawMiniMonitor(dc, Polar(o, (r0 + r1) / 2, a), zone.Rect, s * icon,
                    new Pen(Brush(Mix(lineIdle, Colors.White, h)), 1.5 * s), Brush(Mix(iconIdle, Colors.White, h)));
                dc.Pop();
            }
            dc.Pop();

            string name = st.Selected >= 0 && st.Layout.Zones[st.Selected].Maximize ? "Maximize" : st.Layout.Name;
            dc.PushOpacity(EaseOutCubic(Entrance(2)));
            DrawPill(dc, $"{name}   {st.LayoutIndex + 1}/{st.LayoutCount}", new Point(o.X, o.Y + outer + 26 * s), 15 * s, s);
            DrawPill(dc, SnapHint(st),
                new Point(o.X, o.Y + outer + 56 * s), 12 * s, s);
            dc.Pop();
        }

        /// <summary>Zone layouts as a small map of the screen under the cursor; the full-size preview shows the real target.</summary>
        void DrawMini(DrawingContext dc, OverlayState st, double s, Color accent)
        {
            DrawPreview(dc, s, accent);

            var o = st.Origin;
            var frame = st.Panel;
            frame.Inflate(8 * s, 8 * s);

            var scaled = frame;
            scaled.Transform(new ScaleTransform(MenuScale, MenuScale, o.X, o.Y).Value);
            DrawBackdrop(dc, new RectangleGeometry(scaled, PanelRadius * s * MenuScale, PanelRadius * s * MenuScale));
            dc.PushTransform(new ScaleTransform(MenuScale, MenuScale, o.X, o.Y));
            DrawPanel(dc, frame, s, PanelAlpha);

            var idle = Color.FromArgb(34, 255, 255, 255);
            var idleLine = Color.FromArgb(70, 255, 255, 255);

            for (int i = 0; i < st.Layout.Zones.Count; i++)
            {
                var r = ToScreen(st.Panel, st.Layout.Zones[i].Rect);
                r.Inflate(-3 * s, -3 * s);
                if (r.Width <= 0 || r.Height <= 0) continue;
                double h = i < _hover.Length ? _hover[i] : 0;
                double p = EaseOutCubic(Entrance(i));

                // Zones settle in one after another; the hovered one lifts slightly.
                double k = Lerp(0.85, 1, p) * (1 + 0.03 * h);
                dc.PushOpacity(p);
                dc.PushTransform(new ScaleTransform(k, k, r.X + r.Width / 2, r.Y + r.Height / 2));
                dc.DrawRoundedRectangle(Brush(Mix(idle, accent, h)), new Pen(Brush(Mix(idleLine, accent, h)), 1 * s), r, 6 * s, 6 * s);

                var label = Text((i + 1).ToString(CultureInfo.InvariantCulture), 13 * s,
                    Brush(Color.FromArgb((byte)Lerp(170, 255, h), 255, 255, 255)), s);
                if (label.Width + 4 * s < r.Width && label.Height < r.Height)
                    dc.DrawText(label, new Point(r.X + (r.Width - label.Width) / 2, r.Y + (r.Height - label.Height) / 2));
                dc.Pop();
                dc.Pop();
            }
            dc.Pop();

            var below = new Point(frame.X + frame.Width / 2, frame.Bottom + 14 * s);
            dc.PushOpacity(EaseOutCubic(Entrance(2)));
            DrawPill(dc, $"{st.Layout.Name}   {st.LayoutIndex + 1}/{st.LayoutCount}", below, 15 * s, s);
            DrawPill(dc, SnapHint(st),
                new Point(below.X, below.Y + 30 * s), 12 * s, s);
            dc.Pop();
        }

        /// <summary>Window cards to pick from, with a live preview of the arrangement above them.</summary>
        void DrawArrange(DrawingContext dc, OverlayState st, ArrangeView ar, double s, Color accent)
        {
            dc.DrawRectangle(Brush(Color.FromArgb(110, 0, 0, 0)), null, new Rect(0, 0, ActualWidth * s, ActualHeight * s));

            var centre = new Point(ar.Grid.X + ar.Grid.Width / 2, (ar.Map.Top + ar.Grid.Bottom) / 2);
            dc.PushTransform(new ScaleTransform(MenuScale, MenuScale, centre.X, centre.Y));

            // Preview map: one accent tile per picked window, showing its icon.
            var frame = ar.Map;
            frame.Inflate(8 * s, 8 * s);
            DrawPanel(dc, frame, s);
            dc.DrawRoundedRectangle(Brush(Color.FromArgb(18, 255, 255, 255)), null, ar.Map, 6 * s, 6 * s);

            double empty = 1 - _slotAlpha[0];
            if (empty > 0.01)
            {
                var hint = Text("Click windows to arrange", 13 * s, Brush(Color.FromArgb((byte)(170 * empty), 255, 255, 255)), s);
                hint.MaxTextWidth = ar.Map.Width - 16 * s;
                hint.TextAlignment = TextAlignment.Center;
                dc.DrawText(hint, new Point(ar.Map.X + 8 * s, ar.Map.Y + (ar.Map.Height - hint.Height) / 2));
            }
            for (int i = 0; i < _slot.Length; i++)
            {
                if (_slotAlpha[i] < 0.01) continue;
                var r = _slot[i];
                r.Inflate(-3 * s, -3 * s);
                if (r.Width <= 0 || r.Height <= 0) continue;
                dc.PushOpacity(_slotAlpha[i]);
                dc.DrawRoundedRectangle(Brush(WithAlpha(accent, 225)), null, r, 6 * s, 6 * s);
                var card = i < ar.Picked.Count ? ar.Cards[ar.Picked[i]] : null;
                if (card?.Thumb != null)
                {
                    // The window's snapshot fills its slot, inside an accent outline.
                    var inner = r;
                    inner.Inflate(-2 * s, -2 * s);
                    if (inner.Width > 0 && inner.Height > 0) DrawThumb(dc, card.Thumb, inner, 5 * s, fill: true);
                }
                else
                {
                    double size = Math.Min(24 * s, Math.Min(r.Width, r.Height) - 8 * s);
                    if (card != null && size > 4 * s)
                        DrawAppIcon(dc, card, new Rect(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size), s);
                }
                dc.Pop();
            }

            var title = new Point(centre.X, ar.Map.Bottom + 18 * s);
            DrawPill(dc, $"Arrange   {ar.Picked.Count}/{Arranger.MaxWindows}", title, 15 * s, s);

            bool full = ar.Picked.Count >= Arranger.MaxWindows;
            var cardBg = Color.FromArgb(230, 32, 32, 38);
            var cardHover = Color.FromArgb(240, 54, 54, 62);
            var edge = Color.FromArgb(30, 255, 255, 255);
            for (int i = 0; i < ar.Cards.Count; i++)
            {
                var c = ar.Cards[i];
                var r = c.Bounds;
                double h = i < _hover.Length ? _hover[i] : 0, p = i < _pick.Length ? _pick[i] : 0;
                double e = EaseOutCubic(Entrance(i));
                double k = Lerp(0.9, 1, e) * (1 + 0.02 * h);

                // Once six are picked, the rest dim to show they can't be added.
                dc.PushOpacity(e * (full ? Lerp(0.45, 1, p) : 1));
                dc.PushTransform(new ScaleTransform(k, k, r.X + r.Width / 2, r.Y + r.Height / 2));
                dc.DrawRoundedRectangle(Brush(Mix(cardBg, cardHover, h)), new Pen(Brush(Mix(edge, accent, p)), (1 + 1.5 * p) * s),
                    r, 10 * s, 10 * s);

                // Snapshot area; the big icon stands in until the snapshot arrives, then cross-fades.
                double strip = 46 * s;
                var shot = new Rect(r.X + 8 * s, r.Y + 8 * s, r.Width - 16 * s, r.Height - 8 * s - strip);
                dc.DrawRoundedRectangle(Brush(Color.FromArgb(255, 20, 20, 24)), null, shot, 6 * s, 6 * s);
                double t = i < _thumb.Length ? _thumb[i] : 0;
                if (t < 0.99)
                {
                    double big = 44 * s;
                    dc.PushOpacity(1 - t);
                    DrawAppIcon(dc, c, new Rect(shot.X + (shot.Width - big) / 2, shot.Y + (shot.Height - big) / 2, big, big), s);
                    dc.Pop();
                }
                if (c.Thumb != null && t > 0.01)
                {
                    dc.PushOpacity(t);
                    DrawThumb(dc, c.Thumb, shot, 6 * s, fill: false);
                    dc.Pop();
                }

                double icon = 20 * s;
                double sy = shot.Bottom + (strip - icon) / 2;
                DrawAppIcon(dc, c, new Rect(r.X + 10 * s, sy, icon, icon), s);

                double tx = r.X + 10 * s + icon + 8 * s, tw = r.Right - 10 * s - tx;
                var name = Text(c.Title, 12.5 * s, Brushes.White, s);
                name.MaxTextWidth = Math.Max(1, tw);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                var app = Text(c.App, 10.5 * s, Brush(Color.FromArgb(150, 255, 255, 255)), s);
                app.MaxTextWidth = Math.Max(1, tw);
                app.MaxLineCount = 1;
                app.Trimming = TextTrimming.CharacterEllipsis;
                double th = name.Height + (c.App.Length > 0 ? app.Height : 0);
                double ty = shot.Bottom + (strip - th) / 2;
                dc.DrawText(name, new Point(tx, ty));
                if (c.App.Length > 0) dc.DrawText(app, new Point(tx, ty + name.Height));

                // Order badge pops in over the snapshot's corner when picked.
                if (p > 0.01 && i < _badge.Length && _badge[i] > 0)
                {
                    var at = new Point(shot.Right - 14 * s, shot.Y + 14 * s);
                    double rad = 12 * s * EaseOutBack(p);
                    dc.DrawEllipse(Brush(accent), new Pen(Brush(Color.FromArgb(200, 20, 20, 24)), 2 * s), at, rad, rad);
                    var num = Text(_badge[i].ToString(CultureInfo.InvariantCulture), 12 * s, Brushes.White, s);
                    num.SetFontWeight(FontWeights.SemiBold);
                    dc.PushOpacity(p);
                    dc.DrawText(num, new Point(at.X - num.Width / 2, at.Y - num.Height / 2));
                    dc.Pop();
                }
                dc.Pop();
                dc.Pop();
            }
            dc.Pop();

            dc.PushOpacity(EaseOutCubic(Entrance(2)));
            DrawPill(dc, "Click: pick in order (first gets the most space)    Esc: cancel    " +
                    (st.Toggle ? "Enter or shortcut: arrange" : "release: arrange"),
                new Point(centre.X, ar.Grid.Bottom + 20 * s), 12 * s, s);
            dc.Pop();
        }

        static string SnapHint(OverlayState st) =>
            "Tab / scroll: change layout    Esc: cancel    " + (st.Toggle ? "click: snap" : "release: snap");

        /// <summary>Snapshot clipped to a rounded box: <paramref name="fill"/> crops to cover it, otherwise it fits inside.</summary>
        static void DrawThumb(DrawingContext dc, ImageSource img, Rect box, double radius, bool fill)
        {
            double k = fill ? Math.Max(box.Width / img.Width, box.Height / img.Height)
                            : Math.Min(box.Width / img.Width, box.Height / img.Height);
            double w = img.Width * k, h = img.Height * k;
            dc.PushClip(new RectangleGeometry(box, radius, radius));
            dc.DrawImage(img, new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
            dc.Pop();
        }

        /// <summary>The window's icon, or its first letter on a tile when it has none.</summary>
        static void DrawAppIcon(DrawingContext dc, ArrangeCard card, Rect r, double s)
        {
            if (card.Icon != null) { dc.DrawImage(card.Icon, r); return; }
            dc.DrawRoundedRectangle(Brush(Color.FromArgb(50, 255, 255, 255)), null, r, r.Width / 5, r.Width / 5);
            string letter = card.Title.Length > 0 ? card.Title[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
            var t = Text(letter, r.Height * 0.55, Brushes.White, s);
            dc.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
        }

        /// <summary>Dark rounded panel with a soft drop shadow, shared by the menus.</summary>
        const double PanelRadius = 14;

        /// <summary>Menu background opacity; see-through enough to show the blur when it's on.</summary>
        byte PanelAlpha => _backdrop != null ? (byte)165 : (byte)225;

        static void DrawPanel(DrawingContext dc, Rect frame, double s, byte alpha = 225)
        {
            double radius = PanelRadius * s;
            for (int i = 3; i >= 1; i--)
            {
                var sh = frame;
                sh.Inflate(4 * i * s, 4 * i * s);
                sh.Offset(0, 3 * i * s);
                dc.DrawRoundedRectangle(Brush(Color.FromArgb(26, 0, 0, 0)), null, sh, radius + 4 * i * s, radius + 4 * i * s);
            }
            dc.DrawRoundedRectangle(Brush(Color.FromArgb(alpha, 28, 28, 34)), new Pen(Brush(Color.FromArgb(28, 255, 255, 255)), 1 * s),
                frame, radius, radius);
        }

        static Rect ToScreen(Rect work, NRect n) =>
            new(work.X + n.X * work.Width, work.Y + n.Y * work.Height, n.W * work.Width, n.H * work.Height);

        static void DrawZoneRect(DrawingContext dc, Rect r, double s, Brush fill, Pen pen, double radius)
        {
            r.Inflate(-6 * s, -6 * s);
            if (r.Width <= 0 || r.Height <= 0) return;
            dc.DrawRoundedRectangle(fill, pen, r, radius, radius);
        }

        /// <summary>Little monitor glyph with the zone's portion filled in.</summary>
        static void DrawMiniMonitor(DrawingContext dc, Point center, NRect zone, double s, Pen pen, Brush fill)
        {
            double w = 34 * s, h = 22 * s;
            var frame = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
            dc.DrawRoundedRectangle(null, pen, frame, 2 * s, 2 * s);
            var part = new Rect(frame.X + zone.X * w, frame.Y + zone.Y * h, zone.W * w, zone.H * h);
            part.Inflate(-2 * s, -2 * s);
            if (part.Width > 0 && part.Height > 0)
                dc.DrawRoundedRectangle(fill, null, part, 1 * s, 1 * s);
        }

        static Point Polar(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y - r * Math.Sin(rad)); // screen y grows downward
        }

        static Geometry Wedge(Point c, double inner, double outer, double a1, double a2)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(c, outer, a1), true, true);
                // Increasing math angle runs counterclockwise on screen.
                ctx.ArcTo(Polar(c, outer, a2), new Size(outer, outer), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(Polar(c, inner, a2), true, false);
                ctx.ArcTo(Polar(c, inner, a1), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        void DrawPill(DrawingContext dc, string text, Point centerTop, double size, double s)
        {
            var ft = Text(text, size, Brushes.White, s);
            var box = new Rect(centerTop.X - ft.Width / 2 - 14 * s, centerTop.Y - 4 * s, ft.Width + 28 * s, ft.Height + 8 * s);
            DrawBackdrop(dc, new RectangleGeometry(box, box.Height / 2, box.Height / 2));
            dc.DrawRoundedRectangle(Brush(Color.FromArgb(_backdrop != null ? (byte)165 : (byte)210, 24, 24, 28)), new Pen(Brush(Color.FromArgb(28, 255, 255, 255)), 1 * s),
                box, box.Height / 2, box.Height / 2);
            dc.DrawText(ft, new Point(box.X + 14 * s, box.Y + 4 * s));
        }

        static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, pixelsPerDip);

        // ---- small maths ----

        static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (byte)Math.Round(Lerp(a.A, b.A, t)), (byte)Math.Round(Lerp(a.R, b.R, t)),
            (byte)Math.Round(Lerp(a.G, b.G, t)), (byte)Math.Round(Lerp(a.B, b.B, t)));

        static Rect LerpRect(Rect a, Rect b, double t) =>
            new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Width, b.Width, t), Lerp(a.Height, b.Height, t));

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static double Clamp01(double t) => Math.Clamp(t, 0, 1);

        static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        /// <summary>Ease-out with a small overshoot, for a gentle "pop".</summary>
        static double EaseOutBack(double t)
        {
            const double c1 = 1.2, c3 = c1 + 1;
            return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
        }
    }
}
