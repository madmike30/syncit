namespace SyncIt.Core.Helpers;

/// <summary>Path normalization utilities for consistent cross-context comparisons.</summary>
public static class PathHelper
{
    /// <summary>
    /// Expand environment variables in a path string.
    /// e.g. "%APPDATA%\SyncIt" → "C:\Users\Me\AppData\Roaming\SyncIt"
    /// </summary>
    public static string Expand(string path) =>
        Environment.ExpandEnvironmentVariables(path);

    /// <summary>
    /// Compute a relative path from a root to a file.
    /// Result uses forward slashes and has no leading separator.
    /// e.g. ("C:\Sync", "C:\Sync\docs\report.docx") → "docs/report.docx"
    /// </summary>
    public static string MakeRelative(string rootPath, string filePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(filePath);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{filePath}' is not under root '{rootPath}'");

        return full[root.Length..].Replace('\\', '/');
    }

    /// <summary>
    /// Reconstruct an absolute local path from a root and a relative path.
    /// </summary>
    public static string ToAbsolute(string rootPath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(rootPath, rel));
    }

    /// <summary>
    /// Ensure a directory exists, creating it (and all parents) if not.
    /// </summary>
    public static void EnsureDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath))
            Directory.CreateDirectory(dirPath);
    }

    /// <summary>
    /// Normalize a relative path to use forward slashes for consistent DB storage.
    /// </summary>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Insert a suffix into a filename before its extension.
    /// e.g. ("report.docx", "(conflict)") → "report (conflict).docx"
    /// </summary>
    public static string InsertSuffix(string filePath, string suffix)
    {
        var dir  = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext  = Path.GetExtension(filePath);
        var result = $"{name} {suffix}{ext}";
        return string.IsNullOrEmpty(dir) ? result : Path.Combine(dir, result);
    }
}
