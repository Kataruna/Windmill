using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windmill;

/// <summary>Hold: the menu is open while the shortcut is held and acts on release. Toggle: press once to open, then click or press again.</summary>
internal enum ActivationStyle { Hold, Toggle }

internal sealed class Settings
{
    [JsonConverter(typeof(JsonStringEnumConverter<ActivationStyle>))]
    public ActivationStyle SnapActivation { get; set; } = ActivationStyle.Hold;

    [JsonConverter(typeof(JsonStringEnumConverter<ActivationStyle>))]
    public ActivationStyle ArrangeActivation { get; set; } = ActivationStyle.Hold;

    /// <summary>
    /// Keys that must be held together. Use Alt, Ctrl, Shift or Win (either side), LAlt, RCtrl and so on (that side
    /// only), or a specific System.Windows.Forms.Keys name such as CapsLock or F13.
    /// </summary>
    public string[] TriggerKeys { get; set; } = { "Alt" };

    /// <summary>Recording a shortcut keeps which side's Ctrl, Alt, Shift or Win was pressed.</summary>
    public bool RecordKeySide { get; set; } = false;

    /// <summary>Hold to pick up to six open windows and tile them together. Same key names as <see cref="TriggerKeys"/>.</summary>
    public string[] ArrangeKeys { get; set; } = { "Alt", "Shift" };

    public bool ArrangeEnabled { get; set; } = true;

    public bool SnapEnabled { get; set; } = true;

    /// <summary>How long the trigger must be held, with no other key pressed, before the menu opens.</summary>
    public int HoldDelayMs { get; set; } = 180;

    /// <summary>Size of the radial menu, as a percentage of the default.</summary>
    public int RingSizePercent { get; set; } = 100;

    /// <summary>Frosted-glass blur of the screen behind the radial ring and the zone panel.</summary>
    public bool BlurBehindMenu { get; set; } = false;

    /// <summary>Pixels of space between snapped windows (and the screen edge).</summary>
    public int Gap { get; set; } = 0;

    public SnapStyle SnapStyle { get; set; } = SnapStyle.Native;

    /// <summary>Native style: leave Windows' Snap Assist open after a snap instead of closing it.</summary>
    public bool ShowSnapAssist { get; set; } = false;

    public bool IncludeFancyZones { get; set; } = true;

    /// <summary>While komorebi is running, snap windows it tiles by sending it commands instead of moving them.</summary>
    public bool KomorebiIntegration { get; set; } = true;

    /// <summary>Check GitHub for a newer release at startup and once a day, and say so in the tray.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Windmill");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    // A hand-edited "TriggerKeys": null is then a bad file, not a crash later on.
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, RespectNullableAnnotations = true };

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Through a temporary file, so a crash or full disk mid-write can't lose the settings already saved.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults; the file is rewritten below only if it was missing.
            return new Settings();
        }

        var fresh = new Settings();
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return fresh;
    }
}
