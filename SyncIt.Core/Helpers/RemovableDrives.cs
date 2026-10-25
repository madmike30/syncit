using System.Runtime.InteropServices;
using System.Text;
using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>Volume serials, so a folder on a USB/external disk only syncs when that exact disk is connected.</summary>
public interface IVolumeInfo
{
    /// <summary>Serial (hex) of the volume mounted at <paramref name="root"/> (e.g. "E:\"), or null when nothing is mounted.</summary>
    string? GetSerial(string root);
}

public sealed class WindowsVolumeInfo : IVolumeInfo
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint flags, StringBuilder? fsName, int fsNameSize);

    public string? GetSerial(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root)) return null;
            return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0) ? serial.ToString("X8") : null;
        }
        catch { return null; }
    }
}

public static class RemovableDrives
{
    /// <summary>True when the folder lives on a non-system disk and should be tied to its volume serial.</summary>
    public static bool ShouldBind(string localPath, string? systemDriveRoot = null)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(localPath));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false; // network shares: no
        systemDriveRoot ??= Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return !string.Equals(root, systemDriveRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the disk's serial on the pair when it lives on a removable/external/second disk.</summary>
    public static void Bind(SyncPair pair, IVolumeInfo volumes, string? systemDriveRoot = null)
    {
        if (!ShouldBind(pair.LocalPath, systemDriveRoot)) { pair.VolumeSerial = null; return; }
        pair.VolumeSerial = volumes.GetSerial(Path.GetPathRoot(Path.GetFullPath(pair.LocalPath))!);
    }

    /// <summary>The right disk is connected (or the pair is not bound to one).</summary>
    public static bool IsAvailable(SyncPair pair, IVolumeInfo volumes)
    {
        if (string.IsNullOrEmpty(pair.VolumeSerial)) return true;
        var root = Path.GetPathRoot(Path.GetFullPath(pair.LocalPath));
        return root is not null && string.Equals(volumes.GetSerial(root), pair.VolumeSerial, StringComparison.OrdinalIgnoreCase);
    }
}
