using System.Reflection;
using SyncIt.Core.Helpers;

namespace SyncIt.App;

internal static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static string LogDir => PathHelper.Expand(@"%APPDATA%\SyncIt\logs");
}
