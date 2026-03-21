using System.Text.Json;
using SyncIt.Core.Helpers;

namespace SyncIt.App;

internal sealed class TraySettings
{
    private static readonly string FilePath =
        PathHelper.Expand(@"%APPDATA%\SyncIt\tray-settings.json");

    public bool NotificationsEnabled { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }
}
