using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Windmill;

public partial class SettingsWindow : Window
{
    readonly Controller _controller;
    string[] _triggerKeys;
    string[] _arrangeKeys;

    // Shortcut recording: keys are collected while held, and the shortcut is committed when all are released.
    System.Windows.Controls.Button? _recordingFor; // which shortcut button is recording, if any
    bool _recording => _recordingFor != null;
    readonly List<string> _recorded = new();
    readonly HashSet<Key> _down = new();

    internal SettingsWindow(Controller controller)
    {
        InitializeComponent();
        _controller = controller;

        var s = Settings.Load();
        _triggerKeys = s.TriggerKeys;
        _arrangeKeys = s.ArrangeKeys;
        ShortcutButton.Content = Describe(_triggerKeys);
        ArrangeButton.Content = Describe(_arrangeKeys);
        SnapBox.IsChecked = s.SnapEnabled;
        ShortcutButton.IsEnabled = SnapStyleBox.IsEnabled = s.SnapEnabled;
        ArrangeBox.IsChecked = s.ArrangeEnabled;
        ArrangeButton.IsEnabled = s.ArrangeEnabled;
        ArrangeStyleBox.IsEnabled = s.ArrangeEnabled;
        SnapStyleBox.SelectedIndex = (int)s.SnapActivation;      // items are listed in enum order
        ArrangeStyleBox.SelectedIndex = (int)s.ArrangeActivation;
        DelaySlider.Value = s.HoldDelayMs;
        RingSizeSlider.Value = s.RingSizePercent;
        // Applied live so it can be tried right away; closing without saving restores the saved size.
        RingSizeSlider.ValueChanged += (_, e) =>
        {
            RadialMetrics.Scale = e.NewValue / 100;
            StatusText.Text = "Ring size applied. Save to keep it.";
        };
        GapSlider.Value = s.Gap;
        BlurBox.IsChecked = s.BlurBehindMenu;
        SnapModeBox.SelectedIndex = (int)s.SnapStyle;
        SnapAssistBox.IsChecked = s.ShowSnapAssist;
        SnapAssistBox.Checked += OnSnapAssistChecked; // after loading, so the saved value doesn't trigger the warning
        FancyZonesBox.IsChecked = s.IncludeFancyZones;
        StartupBox.IsChecked = Startup.IsEnabled;
        LayoutsCaption.Text = controller.LayoutSummary;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => StopRecording(commit: false);
        Closed += (_, _) =>
        {
            StopRecording(commit: false);
            _controller.Reload(); // drop any unsaved live preview (ring size)
        };
    }

    static string Describe(string[] keys) => string.Join(" + ", keys);

    void OnSnapToggled(object sender, RoutedEventArgs e)
    {
        if (ShortcutButton == null) return; // fired while the XAML is still loading
        if (SnapBox.IsChecked != true && _recordingFor == ShortcutButton) StopRecording(commit: false);
        ShortcutButton.IsEnabled = SnapStyleBox.IsEnabled = SnapBox.IsChecked == true;
    }

    void OnSnapAssistChecked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Windows Snap Assist is not controlled by Windmill.\n\n" +
            "After each snap it opens a panel that takes keyboard focus from your window, and it may cover " +
            "part of the screen until you pick a window or press Esc. This can make snapping feel jumpy or " +
            "inconsistent, and Windmill can't guarantee how it behaves across Windows updates.\n\n" +
            "Enable Snap Assist anyway?",
            "Enable Snap Assist", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) SnapAssistBox.IsChecked = false;
    }

    void OnArrangeToggled(object sender, RoutedEventArgs e)
    {
        if (ArrangeButton == null) return; // fired while the XAML is still loading
        if (ArrangeBox.IsChecked != true && _recordingFor == ArrangeButton) StopRecording(commit: false);
        ArrangeButton.IsEnabled = ArrangeStyleBox.IsEnabled = ArrangeBox.IsChecked == true;
    }

    void OnRecordShortcut(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        bool same = _recordingFor == button;
        StopRecording(commit: false);
        if (same) return;
        _recordingFor = button;
        _controller.Paused = true; // otherwise holding Alt here would open the menu over this window
        _recorded.Clear();
        _down.Clear();
        button.Content = "Press keys…";
        StatusText.Text = "Hold the keys together, then let go. Esc cancels.";
    }

    void StopRecording(bool commit)
    {
        if (_recordingFor is not { } button) return;
        _recordingFor = null;
        _controller.Paused = false;
        string status = "";
        if (commit && _recorded.Count > 0)
        {
            var keys = _recorded.ToArray();
            var other = button == ShortcutButton ? _arrangeKeys : _triggerKeys;
            if (SameKeys(keys, other)) status = "That's already the other shortcut. Pick a different combination.";
            else
            {
                if (button == ShortcutButton) _triggerKeys = keys;
                else _arrangeKeys = keys;
                status = "Shortcut changed. Save to apply.";
            }
        }
        ShortcutButton.Content = Describe(_triggerKeys);
        ArrangeButton.Content = Describe(_arrangeKeys);
        StatusText.Text = status;
    }

    static bool SameKeys(string[] a, string[] b) =>
        a.Length == b.Length && a.All(k => b.Contains(k, StringComparer.OrdinalIgnoreCase));

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key == Key.Escape && _recorded.Count == 0) { StopRecording(commit: false); return; }

        string? name = NameOf(key);
        if (name == null) return;
        _down.Add(key);
        if (!_recorded.Contains(name)) _recorded.Add(name);
        _recordingFor!.Content = Describe(_recorded.ToArray());
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        _down.Remove(e.Key == Key.System ? e.SystemKey : e.Key);
        if (_down.Count == 0 && _recorded.Count > 0) StopRecording(commit: true);
    }

    /// <summary>Modifiers become side-agnostic aliases; everything else uses its Windows Forms key name.</summary>
    static string? NameOf(Key key)
    {
        switch (key)
        {
            case Key.LeftAlt or Key.RightAlt: return "Alt";
            case Key.LeftCtrl or Key.RightCtrl: return "Ctrl";
            case Key.LeftShift or Key.RightShift: return "Shift";
            case Key.LWin or Key.RWin: return "Win";
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : ((System.Windows.Forms.Keys)vk).ToString();
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var s = new Settings
        {
            TriggerKeys = _triggerKeys,
            ArrangeKeys = _arrangeKeys,
            SnapEnabled = SnapBox.IsChecked == true,
            ArrangeEnabled = ArrangeBox.IsChecked == true,
            SnapActivation = (ActivationStyle)Math.Max(0, SnapStyleBox.SelectedIndex),
            ArrangeActivation = (ActivationStyle)Math.Max(0, ArrangeStyleBox.SelectedIndex),
            HoldDelayMs = (int)DelaySlider.Value,
            RingSizePercent = (int)RingSizeSlider.Value,
            BlurBehindMenu = BlurBox.IsChecked == true,
            Gap = (int)GapSlider.Value,
            SnapStyle = (SnapStyle)Math.Max(0, SnapModeBox.SelectedIndex),
            ShowSnapAssist = SnapAssistBox.IsChecked == true,
            IncludeFancyZones = FancyZonesBox.IsChecked == true,
        };

        try
        {
            s.Save();
            _controller.Reload();
            Startup.Set(StartupBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Couldn't save: " + ex.Message;
            return;
        }

        LayoutsCaption.Text = _controller.LayoutSummary;
        StatusText.Text = "Saved";
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
