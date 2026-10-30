using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace Windmill;

internal sealed class ArrangeCard
{
    public IntPtr Hwnd;
    public string Title = "";
    public string App = "";
    public ImageSource? Icon;
    public ImageSource? Thumb; // snapshot of the window; filled in on the UI thread as captures finish
    public Rect Bounds;        // physical pixels relative to the overlay monitor
}

/// <summary>One auto-arrange session: the window cards, which ones are picked (in click order), and where everything is drawn.</summary>
internal sealed class ArrangeView
{
    public List<ArrangeCard> Cards = new();
    public List<int> Picked = new();
    public bool Portrait;
    public Rect Map;    // miniature preview of the arrangement
    public Rect Grid;   // bounding box of the cards

    public int HitTest(Point p) => Cards.FindIndex(c => c.Bounds.Contains(p));
}

/// <summary>Lists arrangeable windows and tiles a chosen set of them across a monitor.</summary>
internal static class Arranger
{
    public const int MaxWindows = 6;
    public const double CardWidth = 220; // at 96 dpi; snapshots are captured at this size in physical pixels

    // Last snapshot of each window, shown immediately next time while a fresh one is captured. UI thread only.
    static readonly Dictionary<IntPtr, ImageSource> ThumbCache = new();

    /// <summary>
    /// Visible, non-minimized top-level app windows, front-most first. Given a monitor, only the windows on it and on
    /// the current virtual desktop, minimized ones included, except those in <paramref name="skip"/>.
    /// </summary>
    public static List<ArrangeCard> FindWindows(IntPtr monitor = default, IReadOnlySet<long>? skip = null)
    {
        var result = new List<ArrangeCard>();
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (IsCandidate(h, self, includeMinimized: monitor != IntPtr.Zero)
                && (monitor == IntPtr.Zero || (Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST) == monitor
                                               && Native.OnCurrentDesktop(h) && skip?.Contains(h.ToInt64()) != true)))
            {
                bool minimized = Native.IsIconic(h);
                var app = AppName(h);
                result.Add(new ArrangeCard
                {
                    Hwnd = h, Title = Native.TextOf(h), Icon = IconOf(h),
                    App = minimized ? (app.Length > 0 ? app + " · minimized" : "Minimized") : app,
                    Thumb = ThumbCache.GetValueOrDefault(h),
                });
            }
            return true;
        }, IntPtr.Zero);

        foreach (var gone in ThumbCache.Keys.Except(result.Select(c => c.Hwnd)).ToList())
            ThumbCache.Remove(gone);
        return result;
    }

    /// <summary>
    /// Snapshots each card's window in the background, front-most first, handing each result to the UI thread.
    /// One at a time so a screen full of large windows doesn't spike memory.
    /// </summary>
    public static void CaptureThumbnails(IReadOnlyList<ArrangeCard> cards, int maxSize, Dispatcher ui, CancellationToken stop)
    {
        var targets = cards.Select(c => (card: c, hwnd: c.Hwnd)).ToList();
        Task.Run(() =>
        {
            foreach (var (card, hwnd) in targets)
            {
                if (stop.IsCancellationRequested) return;
                var thumb = Capture(hwnd, maxSize);
                if (thumb != null)
                    ui.BeginInvoke(() => { card.Thumb = thumb; ThumbCache[hwnd] = thumb; });
            }
        }, stop);
    }

    static BitmapSource? Capture(IntPtr h, int maxSize)
    {
        if (!Native.IsWindow(h) || Native.IsHungAppWindow(h)) return null; // PrintWindow would wait on it
        if (Native.IsIconic(h)) return null; // nothing to print; the card keeps its icon (or the last snapshot)

        // Crop the invisible resize borders, same as when snapping.
        var visible = WindowSnapper.VisibleRect(h, out var outer);
        if (outer.Width <= 0 || outer.Height <= 0 || visible.Width <= 0 || visible.Height <= 0) return null;

        try
        {
            using var full = new SD.Bitmap(outer.Width, outer.Height, SDI.PixelFormat.Format32bppRgb);
            using (var g = SD.Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool ok;
                try { ok = Native.PrintWindow(h, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok) return null;
            }

            double k = Math.Min(1, (double)maxSize / Math.Max(visible.Width, visible.Height));
            int w = Math.Max(1, (int)(visible.Width * k)), hh = Math.Max(1, (int)(visible.Height * k));
            using var small = new SD.Bitmap(w, hh, SDI.PixelFormat.Format32bppRgb);
            using (var g = SD.Graphics.FromImage(small))
            {
                g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(full, new SD.Rectangle(0, 0, w, hh),
                    new SD.Rectangle(visible.Left - outer.Left, visible.Top - outer.Top, visible.Width, visible.Height),
                    SD.GraphicsUnit.Pixel);
            }

            var data = small.LockBits(new SD.Rectangle(0, 0, w, hh), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
            try
            {
                var src = BitmapSource.Create(w, hh, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * hh, data.Stride);
                src.Freeze();
                return src;
            }
            finally { small.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException)
        {
            return null; // window vanished or is too large to copy; the card keeps its icon
        }
    }

    static bool IsCandidate(IntPtr h, uint self, bool includeMinimized)
    {
        if (!WindowSnapper.IsSnappable(h) || (!includeMinimized && Native.IsIconic(h))) return false;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false; // dialogs, popups

        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;

        // Cloaked: suspended store apps and windows on other virtual desktops.
        if (Native.DwmGetWindowAttributeInt(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid != self && Native.GetWindowTextLength(h) > 0;
    }

    static string AppName(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName == "ApplicationFrameHost" ? "" : p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ""; // exited meanwhile
        }
    }

    static ImageSource? IconOf(IntPtr h)
    {
        // Ask the window first (a hung app must not stall the menu), then fall back to its class icon.
        IntPtr icon = Ask(h, Native.ICON_BIG);
        if (icon == IntPtr.Zero) icon = Ask(h, Native.ICON_SMALL2);
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, Native.GCLP_HICON);
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, Native.GCLP_HICONSM);
        if (icon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        static IntPtr Ask(IntPtr h, int size) =>
            Native.SendMessageTimeout(h, Native.WM_GETICON, (IntPtr)size, IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG, 40, out var r) == IntPtr.Zero ? IntPtr.Zero : r;
    }

    /// <summary>Lays out the preview map and a grid of cards centred in <paramref name="work"/> (physical pixels).</summary>
    public static ArrangeView Plan(List<ArrangeCard> cards, Rect work, double dpi)
    {
        double cw = CardWidth * dpi, ch = 168 * dpi, gap = 12 * dpi;
        double mapH = 130 * dpi, header = 56 * dpi, footer = 72 * dpi, margin = 32 * dpi;

        int cols = Math.Max(1, Math.Min(Math.Min(5, cards.Count), (int)((work.Width - 2 * margin + gap) / (cw + gap))));
        int maxRows = Math.Max(1, (int)((work.Height - 2 * margin - mapH - header - footer + gap) / (ch + gap)));
        int rows = Math.Min(maxRows, (cards.Count + cols - 1) / cols);
        if (cards.Count > rows * cols) cards = cards.Take(rows * cols).ToList(); // keep the most recently used

        double gridW = cols * (cw + gap) - gap, gridH = rows * (ch + gap) - gap;
        double mapW = mapH * work.Width / work.Height;
        double top = work.Y + (work.Height - (mapH + header + gridH + footer)) / 2;

        var view = new ArrangeView
        {
            Cards = cards,
            Portrait = work.Height > work.Width,
            Map = new Rect(work.X + (work.Width - mapW) / 2, top, mapW, mapH),
            Grid = new Rect(work.X + (work.Width - gridW) / 2, top + mapH + header, gridW, gridH),
        };
        for (int i = 0; i < cards.Count; i++)
            cards[i].Bounds = new Rect(view.Grid.X + i % cols * (cw + gap), view.Grid.Y + i / cols * (ch + gap), cw, ch);
        return view;
    }

    /// <summary>Where each of <paramref name="n"/> windows goes; the first slot is always the largest.</summary>
    public static IReadOnlyList<NRect> Slots(int n, bool portrait)
    {
        NRect[] slots = n switch
        {
            <= 0 => Array.Empty<NRect>(),
            1 => new NRect[] { new(0, 0, 1, 1) },
            2 => new NRect[] { new(0, 0, 0.5, 1), new(0.5, 0, 0.5, 1) },
            3 => new NRect[] { new(0, 0, 0.5, 1), new(0.5, 0, 0.5, 0.5), new(0.5, 0.5, 0.5, 0.5) },
            4 => new NRect[] { new(0, 0, 0.5, 0.5), new(0.5, 0, 0.5, 0.5), new(0, 0.5, 0.5, 0.5), new(0.5, 0.5, 0.5, 0.5) },
            5 => new NRect[]
            {
                new(0, 0, 0.5, 1),
                new(0.5, 0, 0.25, 0.5), new(0.75, 0, 0.25, 0.5),
                new(0.5, 0.5, 0.25, 0.5), new(0.75, 0.5, 0.25, 0.5),
            },
            _ => Enumerable.Range(0, 6).Select(i => new NRect(i % 3 / 3.0, i / 3 * 0.5, 1 / 3.0, 0.5)).ToArray(),
        };
        // On a portrait screen, stack instead of putting side by side.
        return portrait ? slots.Select(r => new NRect(r.Y, r.X, r.H, r.W)).ToArray() : slots;
    }

    /// <param name="remember">Restore each window's size when it is dragged away (Native snap style).</param>
    public static void Arrange(IReadOnlyList<IntPtr> windows, Native.RECT work, int gap, bool remember)
    {
        var slots = Slots(windows.Count, work.Height > work.Width);
        for (int i = 0; i < windows.Count; i++)
            // Always move and resize: real Windows snap of several windows in a row fights its own Snap Assist.
            WindowSnapper.Place(windows[i], work, new Zone(slots[i], Maximize: windows.Count == 1), gap, remember);

        // Bring them all forward, first pick on top. Windows ignores HWND_TOP from a background process, but making
        // a window topmost and back again always lifts it above every normal window.
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if ((Native.GetWindowLong(windows[i], Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0) continue; // already above
            Native.SetWindowPos(windows[i], Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            Native.SetWindowPos(windows[i], Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        }
        if (windows.Count > 0) Native.SetForegroundWindow(windows[0]);
    }
}
