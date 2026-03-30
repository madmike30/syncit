using System.Text;

namespace SyncIt.Core.Helpers;

/// <summary>Path normalization utilities for consistent cross-context comparisons.</summary>
public static class PathHelper
{
    private static readonly IReadOnlyDictionary<char, string> RemoteNameReplacements = new Dictionary<char, string>
    {
        ['<'] = "\uFF1C",
        ['>'] = "\uFF1E",
        [':'] = "\uFF1A",
        ['"'] = "\uFF02",
        ['/'] = "\uFF0F",
        ['\\'] = "\uFF3C",
        ['|'] = "\uFF5C",
        ['?'] = "\uFF1F",
        ['*'] = "\uFF0A",
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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
    /// Sanitizes a remote Drive name segment into a Windows-safe local path segment.
    /// Uses full-width Unicode lookalikes for invalid filename characters to preserve readability.
    /// </summary>
    public static string SanitizeRemoteSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return "_";

        var builder = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if (RemoteNameReplacements.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
            }
            else if (char.IsControl(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(sanitized))
            sanitized = "_";

        if (sanitized is "." or "..")
            sanitized = sanitized.Replace('.', '_');

        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedDeviceNames.Contains(baseName))
            sanitized = baseName + "_" + Path.GetExtension(sanitized);

        return sanitized;
    }

    /// <summary>
    /// Joins a sanitized parent path and a raw remote name into a normalized relative path.
    /// </summary>
    public static string CombineRemoteRelativePath(string parentPath, string remoteName)
    {
        var safeName = SanitizeRemoteSegment(remoteName);
        var normalizedParent = Normalize(parentPath);
        return string.IsNullOrEmpty(normalizedParent)
            ? safeName
            : $"{normalizedParent}/{safeName}";
    }

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
