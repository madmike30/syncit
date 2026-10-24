using System.Text;

namespace SyncIt.Core.Helpers;

/// <summary>
/// Compares relative paths the way a user sees them: case-insensitive (Windows) and
/// Unicode-normalization-insensitive. A name typed on a Mac or uploaded from a phone can
/// use decomposed characters (NFD) while Windows uses composed ones (NFC); without this
/// the same file looked like two different files.
/// </summary>
public sealed class PathKey : IEqualityComparer<string>
{
    public static readonly PathKey Comparer = new();

    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        try { return p.IsNormalized(NormalizationForm.FormC) ? p : p.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return p; } // invalid surrogate: compare as-is
    }

    public bool Equals(string? x, string? y) =>
        x is null || y is null ? x == y : string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));

    /// <summary>True when two paths differ only by letter case (a case-only rename).</summary>
    public static bool IsCaseOnlyChange(string oldPath, string newPath) =>
        !string.Equals(Normalize(oldPath), Normalize(newPath), StringComparison.Ordinal)
        && string.Equals(Normalize(oldPath), Normalize(newPath), StringComparison.OrdinalIgnoreCase);
}
