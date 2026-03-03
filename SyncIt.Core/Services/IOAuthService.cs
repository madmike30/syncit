using Google.Apis.Auth.OAuth2;

namespace SyncIt.Core.Services;

public interface IOAuthService
{
    /// <summary>
    /// Initialize OAuth. Loads existing token from disk (DPAPI-encrypted).
    /// If no token exists, triggers browser-based OAuth consent flow.
    /// </summary>
    Task<UserCredential> InitializeAsync(CancellationToken ct = default);

    /// <summary>Returns the current credential (null if not yet initialized).</summary>
    UserCredential? Credential { get; }

    /// <summary>True if a valid token is available (may still need refresh).</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Force a new browser-based auth flow. Used when the refresh token has expired.
    /// </summary>
    Task<UserCredential> ReauthorizeAsync(CancellationToken ct = default);
}
