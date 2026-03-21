using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SyncIt.App;

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
            key.SetValue(ValueName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
        else
            key.DeleteValue(ValueName, false);
    }
}
