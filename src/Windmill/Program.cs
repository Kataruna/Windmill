using System;
using System.Linq;
using System.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Windmill;

internal static class Program
{
    static SettingsWindow? _settingsWindow;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Windmill.SingleInstance", out bool first);
        if (!first) return;

        // The overlay reads touch and pen itself (WM_POINTER*); WPF's own stylus stack could take those messages first.
        // The settings window still works by touch, as plain mouse input.
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        Controller controller;
        try
        {
            controller = new Controller();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            WF.MessageBox.Show("Windmill could not start:\n" + ex.Message + "\n\nCheck TriggerKeys in " + Settings.FilePath,
                "Windmill", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error);
            return;
        }

        using var tray = BuildTray(controller, app);
        if (args.Contains("--settings")) ShowSettings(controller);
        app.Run();
        controller.Dispose();
    }

    static void ShowSettings(Controller controller)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(controller);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    static WF.NotifyIcon BuildTray(Controller controller, System.Windows.Application app)
    {
        var enabled = new WF.ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => controller.Enabled = enabled.Checked;

        var startup = new WF.ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) => Startup.Set(!Startup.IsEnabled);

        var update = new WF.ToolStripMenuItem { Visible = false };
        var updateSeparator = new WF.ToolStripSeparator { Visible = false };

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => startup.Checked = Startup.IsEnabled; // may have changed in the settings window
        menu.Items.Add(update);
        menu.Items.Add(updateSeparator);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings(controller));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(enabled);
        menu.Items.Add(startup);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());

        var icon = new WF.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Windmill",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => ShowSettings(controller);
        controller.Warning += message => icon.ShowBalloonTip(8000, "Windmill", message, WF.ToolTipIcon.Warning);

        // A newer release: say so once, and keep a menu item for it since the notification goes away.
        Release? release = null;
        bool updateBalloon = false; // the one showing is about the update, not a warning
        update.Click += (_, _) => { if (release != null) OpenUrl(release.Url); };
        icon.BalloonTipClicked += (_, _) => { if (updateBalloon && release != null) OpenUrl(release.Url); };
        controller.Warning += _ => updateBalloon = false;
        async void CheckForUpdate()
        {
            if (!Settings.Load().CheckForUpdates) return; // read each time so the settings window's choice applies
            var found = await Updater.CheckAsync();
            if (found == null || found.Version == release?.Version) return;
            release = found;
            update.Text = $"Update to {found.Version}…";
            update.Visible = updateSeparator.Visible = true;
            updateBalloon = true;
            icon.ShowBalloonTip(10000, "Windmill update available",
                $"Version {found.Version} is out. Click to download it.", WF.ToolTipIcon.Info);
        }
        var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        updateTimer.Tick += (_, _) => { updateTimer.Interval = TimeSpan.FromHours(24); CheckForUpdate(); };
        updateTimer.Start();

        // Follow the taskbar when the Windows theme changes. The event can arrive on another thread.
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            ui.BeginInvoke(() =>
            {
                var old = icon.Icon;
                icon.Icon = MakeIcon();
                old?.Dispose();
            });
        };
        return icon;
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { } // no browser registered
    }

    /// <summary>The logo at tray size: all white on a dark taskbar, in colour on a light one.</summary>
    static SD.Icon MakeIcon()
    {
        int size = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
        using var bmp = Logo.Render(size, TaskbarIsLight() ? null : SD.Color.White);
        IntPtr handle = bmp.GetHicon();
        try { return (SD.Icon)SD.Icon.FromHandle(handle).Clone(); } // the clone owns its own copy
        finally { DestroyIcon(handle); }
    }

    /// <summary>The taskbar's theme (Settings > Personalization > Colors > "Choose your default Windows mode").</summary>
    static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0; // missing on old Windows 10: dark
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
