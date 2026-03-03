namespace SyncIt.Core.Models;

/// <summary>
/// Maps Google Workspace MIME types to their preferred export format.
/// Google Docs/Sheets/Slides cannot be downloaded directly — they must be exported.
/// </summary>
public static class GoogleDocExportMap
{
    /// <summary>Maps Drive MIME → (export MIME, file extension).</summary>
    public static readonly IReadOnlyDictionary<string, (string ExportMime, string Extension)> Map =
        new Dictionary<string, (string, string)>
        {
            ["application/vnd.google-apps.document"]     = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
            ["application/vnd.google-apps.spreadsheet"]  = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",       ".xlsx"),
            ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation",".pptx"),
            ["application/vnd.google-apps.drawing"]      = ("image/svg+xml",                                                           ".svg"),
            ["application/vnd.google-apps.form"]         = ("application/pdf",                                                         ".pdf"),
            ["application/vnd.google-apps.script"]       = ("application/vnd.google-apps.script+json",                                 ".json"),
        };

    /// <summary>Returns true if the MIME type is a Google Workspace type that needs export.</summary>
    public static bool IsGoogleDoc(string? mimeType) =>
        mimeType is not null && Map.ContainsKey(mimeType);

    /// <summary>
    /// Gets the export info for a Google Doc. Returns false if not a Google Doc.
    /// </summary>
    public static bool TryGetExport(string? mimeType, out string exportMime, out string extension)
    {
        if (mimeType is not null && Map.TryGetValue(mimeType, out var info))
        {
            (exportMime, extension) = info;
            return true;
        }
        exportMime = string.Empty;
        extension  = string.Empty;
        return false;
    }
}
