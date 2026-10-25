using SyncIt.Core.Models;

namespace SyncIt.Core.Helpers;

/// <summary>Local copies exported from Google Docs/Sheets/Slides.</summary>
public static class ExportedFiles
{
    /// <summary>True when the remote baseline is a native Google file (so the local file is only an export).</summary>
    public static bool IsExportOf(FileRecord? remoteBaseline) =>
        remoteBaseline?.MimeType is { } m && GoogleDocExportMap.IsGoogleDoc(m);

    public static void MarkReadOnly(string path)
    {
        try { if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); } catch { }
    }

    public static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        catch { }
    }
}
