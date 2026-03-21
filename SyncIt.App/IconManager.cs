using System.Drawing;
using System.Runtime.Versioning;

namespace SyncIt.App;

[SupportedOSPlatform("windows")]
public sealed class IconManager : IDisposable
{
    public Icon IdleIcon    { get; }
    public Icon SyncingIcon { get; }
    public Icon PausedIcon  { get; }
    public Icon ErrorIcon   { get; }
    public Icon WarningIcon { get; }

    public IconManager()
    {
        IdleIcon    = LoadOrGenerate("icon_idle",    Color.FromArgb(0, 120, 215));
        SyncingIcon = LoadOrGenerate("icon_syncing", Color.FromArgb(0, 183, 74));
        PausedIcon  = LoadOrGenerate("icon_paused",  Color.FromArgb(150, 150, 150));
        ErrorIcon   = LoadOrGenerate("icon_error",   Color.FromArgb(232, 17, 35));
        WarningIcon = LoadOrGenerate("icon_warning", Color.FromArgb(255, 185, 0));
    }

    private static Icon LoadOrGenerate(string resourceName, Color fallbackColor)
    {
        var asm  = typeof(IconManager).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName + ".ico", StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            return new Icon(stream, 16, 16);
        }

        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.Clear(fallbackColor);
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
