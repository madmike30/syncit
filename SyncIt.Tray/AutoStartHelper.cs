using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SyncIt.Tray;

/// <summary>
/// Manages the "Start with Windows" registry entry for the tray app.
/// Uses HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutoStartHelper
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SyncIt";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (key is null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
