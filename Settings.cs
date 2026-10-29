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
    /// Keys that must be held together. Use Alt, Ctrl, Shift or Win (either side), or a specific
    /// System.Windows.Forms.Keys name such as LMenu, RControlKey, CapsLock, F13.
    /// </summary>
    public string[] TriggerKeys { get; set; } = { "Alt" };

    /// <summary>Hold to pick up to six open windows and tile them together. Same key names as <see cref="TriggerKeys"/>.</summary>
    public string[] ArrangeKeys { get; set; } = { "Alt", "Shift" };

    public bool ArrangeEnabled { get; set; } = true;

    /// <summary>How long the trigger must be held, with no other key pressed, before the menu opens.</summary>
    public int HoldDelayMs { get; set; } = 180;

    /// <summary>Size of the radial menu, as a percentage of the default.</summary>
    public int RingSizePercent { get; set; } = 100;

    /// <summary>Pixels of space between snapped windows (and the screen edge).</summary>
    public int Gap { get; set; } = 0;

    public bool IncludeFancyZones { get; set; } = true;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Windmill");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
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
        catch (IOException) { }
        return fresh;
    }
}
