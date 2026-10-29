using System;
using Microsoft.Win32;

namespace Windmill;

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Windmill") != null;
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (on) key.SetValue("Windmill", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("Windmill", throwOnMissingValue: false);
    }
}
