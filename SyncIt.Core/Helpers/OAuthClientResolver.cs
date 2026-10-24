using System.Reflection;

namespace SyncIt.Core.Helpers;

public enum OAuthClientSource { Custom, BuiltIn }

public sealed record OAuthClient(string ClientId, string ClientSecret, OAuthClientSource Source);

/// <summary>
/// Picks the OAuth client SyncIt signs in with:
/// 1) a custom client the user saved in Windows Credential Manager ("advanced" mode), else
/// 2) the built-in client baked in at build time from SYNCIT_GOOGLE_CLIENT_ID / SYNCIT_GOOGLE_CLIENT_SECRET
///    (GitHub Actions secrets or local environment variables; never committed), else none.
/// </summary>
public static class OAuthClientResolver
{
    public const string ClientIdMetadataKey     = "SyncIt.GoogleClientId";
    public const string ClientSecretMetadataKey = "SyncIt.GoogleClientSecret";

    /// <summary>Pure selection logic (unit-tested).</summary>
    public static OAuthClient? Resolve((string Id, string Secret)? custom, string? builtInId, string? builtInSecret)
    {
        if (custom is { } c && OAuthClientValidator.ValidateClientId(c.Id?.Trim()) is null && !string.IsNullOrWhiteSpace(c.Secret))
            return new OAuthClient(c.Id.Trim(), c.Secret.Trim(), OAuthClientSource.Custom);

        if (IsValidBuiltIn(builtInId, builtInSecret))
            return new OAuthClient(builtInId!.Trim(), builtInSecret!.Trim(), OAuthClientSource.BuiltIn);

        return null;
    }

    // Trim: build secrets set from a shell often carry a trailing newline, which made a
    // perfectly good built-in client look invalid (sign-in fell back to the own-client page).
    public static bool IsValidBuiltIn(string? id, string? secret) =>
        OAuthClientValidator.ValidateClientId(id?.Trim()) is null && !string.IsNullOrWhiteSpace(secret);

    /// <summary>Reads the build-time client from assembly metadata.</summary>
    public static (string? Id, string? Secret) ReadBuiltIn(Assembly? assembly = null)
    {
        var meta = (assembly ?? typeof(OAuthClientResolver).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Get(string key) => meta.FirstOrDefault(m => m.Key == key)?.Value;
        return (Get(ClientIdMetadataKey), Get(ClientSecretMetadataKey));
    }

    public static bool HasBuiltInClient
    {
        get { var (id, s) = ReadBuiltIn(); return IsValidBuiltIn(id, s); }
    }

    /// <summary>Resolves using Credential Manager + build-time client. Throws if neither exists.</summary>
    public static OAuthClient ResolveForApp()
    {
        (string, string)? custom = null;
        if (CredentialHelper.CredentialsExist())
        {
            try { custom = CredentialHelper.LoadClientCredentials(); } catch { /* fall back to built-in */ }
        }
        var (id, secret) = ReadBuiltIn();
        return Resolve(custom, id, secret)
            ?? throw new InvalidOperationException(
                "No Google sign-in client is configured. Open SyncIt setup and choose \"Use my own Google client (advanced)\".");
    }
}
