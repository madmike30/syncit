using System.Drawing;
using System.Runtime.Versioning;

namespace SyncIt.Tray;

/// <summary>
/// Manages the set of tray icons for each sync state.
/// Icons are embedded resources loaded at startup.
/// Falls back to a generated colored icon if resource files are missing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconManager : IDisposable
{
    public Icon IdleIcon    { get; }
    public Icon SyncingIcon { get; }
    public Icon PausedIcon  { get; }
    public Icon ErrorIcon   { get; }
    public Icon WarningIcon { get; }

    public TrayIconManager()
    {
        IdleIcon    = LoadOrGenerate("icon_idle",    Color.FromArgb(0, 120, 215));   // blue
        SyncingIcon = LoadOrGenerate("icon_syncing", Color.FromArgb(0, 183, 74));    // green
        PausedIcon  = LoadOrGenerate("icon_paused",  Color.FromArgb(150, 150, 150)); // gray
        ErrorIcon   = LoadOrGenerate("icon_error",   Color.FromArgb(232, 17, 35));   // red
        WarningIcon = LoadOrGenerate("icon_warning", Color.FromArgb(255, 185, 0));   // amber
    }

    private static Icon LoadOrGenerate(string resourceName, Color fallbackColor)
    {
        var asm  = typeof(TrayIconManager).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName + ".ico", StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            return new Icon(stream, 16, 16);
        }

        // Generate a simple colored square icon as fallback
        return GenerateIcon(fallbackColor);
    }

    private static Icon GenerateIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.Clear(color);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        IdleIcon.Dispose();
        SyncingIcon.Dispose();
        PausedIcon.Dispose();
        ErrorIcon.Dispose();
        WarningIcon.Dispose();
    }
}
