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

    public const string DocMime = "application/vnd.google-apps.document";
    public const string SheetMime = "application/vnd.google-apps.spreadsheet";
    public const string SlidesMime = "application/vnd.google-apps.presentation";

    /// <summary>Per-folder format choices (1.4+). Key = format name.</summary>
    public static readonly IReadOnlyDictionary<string, (string ExportMime, string Extension)> DocFormats = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["odt"]  = ("application/vnd.oasis.opendocument.text", ".odt"),
        ["pdf"]  = ("application/pdf", ".pdf"),
        ["rtf"]  = ("application/rtf", ".rtf"),
        ["txt"]  = ("text/plain", ".txt"),
    };
    public static readonly IReadOnlyDictionary<string, (string ExportMime, string Extension)> SheetFormats = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["ods"]  = ("application/vnd.oasis.opendocument.spreadsheet", ".ods"),
        ["pdf"]  = ("application/pdf", ".pdf"),
        ["csv"]  = ("text/csv", ".csv"),
    };
    public static readonly IReadOnlyDictionary<string, (string ExportMime, string Extension)> SlideFormats = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        ["odp"]  = ("application/vnd.oasis.opendocument.presentation", ".odp"),
        ["pdf"]  = ("application/pdf", ".pdf"),
    };

    /// <summary>Export for a Google file using the folder's format choices; unknown choices fall back to the default.</summary>
    public static bool TryGetExport(string? mimeType, SyncPair pair, out string exportMime, out string extension)
    {
        IReadOnlyDictionary<string, (string, string)>? table = mimeType switch
        {
            DocMime => DocFormats,
            SheetMime => SheetFormats,
            SlidesMime => SlideFormats,
            _ => null,
        };
        var choice = mimeType switch
        {
            DocMime => pair.DocsExportFormat,
            SheetMime => pair.SheetsExportFormat,
            SlidesMime => pair.SlidesExportFormat,
            _ => null,
        };
        if (table is not null && choice is not null && table.TryGetValue(choice, out var picked))
        {
            (exportMime, extension) = picked;
            return true;
        }
        return TryGetExport(mimeType, out exportMime, out extension);
    }

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
