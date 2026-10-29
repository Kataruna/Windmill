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

        var menu = new WF.ContextMenuStrip();
        menu.Opening += (_, _) => startup.Checked = Startup.IsEnabled; // may have changed in the settings window
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
        return icon;
    }

    static SD.Icon MakeIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            SD.Color[] colors = { SD.Color.FromArgb(59, 130, 246), SD.Color.FromArgb(34, 197, 94), SD.Color.FromArgb(250, 204, 21), SD.Color.FromArgb(239, 68, 68) };
            // Four pinwheel blades, one per quadrant.
            SD.Point[][] blades =
            {
                new[] { new SD.Point(16, 16), new SD.Point(16, 2), new SD.Point(28, 4) },
                new[] { new SD.Point(16, 16), new SD.Point(30, 16), new SD.Point(28, 28) },
                new[] { new SD.Point(16, 16), new SD.Point(16, 30), new SD.Point(4, 28) },
                new[] { new SD.Point(16, 16), new SD.Point(2, 16), new SD.Point(4, 4) },
            };
            for (int i = 0; i < 4; i++)
            {
                using var brush = new SD.SolidBrush(colors[i]);
                g.FillPolygon(brush, blades[i]);
            }
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }
}
