using System.Text.RegularExpressions;

namespace SyncIt.Core.Helpers;

/// <summary>Format checks for user-pasted Google OAuth desktop client credentials.</summary>
public static partial class OAuthClientValidator
{
    [GeneratedRegex(@"^\d{6,}-[a-z0-9]{10,}\.apps\.googleusercontent\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex ClientIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex SecretRegex();

    /// <summary>Returns null when valid, otherwise a user-facing error message.</summary>
    public static string? ValidateClientId(string? clientId)
    {
        var v = clientId?.Trim() ?? string.Empty;
        if (v.Length == 0) return "Paste the Client ID from Google Cloud Console.";
        if (!ClientIdRegex().IsMatch(v))
            return "That doesn't look like a Client ID. It should end with .apps.googleusercontent.com.";
        return null;
    }

    /// <summary>Returns null when valid, otherwise a user-facing error message.</summary>
    public static string? ValidateClientSecret(string? clientSecret)
    {
        var v = clientSecret?.Trim() ?? string.Empty;
        if (v.Length == 0) return "Paste the Client Secret from Google Cloud Console.";
        if (v.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            return "You pasted the Client ID into the Client Secret box.";
        if (!SecretRegex().IsMatch(v))
            return "That doesn't look like a Client Secret (usually starts with GOCSPX-, no spaces).";
        return null;
    }
}
