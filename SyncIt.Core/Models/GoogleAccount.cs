namespace SyncIt.Core.Models;

/// <summary>A signed-in Google account. Tokens are never stored in syncit.json, only the path of the
/// DPAPI-encrypted token file (current Windows user only).</summary>
public class GoogleAccount
{
    /// <summary>Stable local ID, referenced by <see cref="SyncPair.AccountId"/>.</summary>
    public string Id { get; set; } = "acc-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>E-mail shown in the UI (filled after sign-in).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted token file, e.g. %APPDATA%\SyncIt\accounts\acc-1234abcd.bin.</summary>
    public string TokenStorePath { get; set; } = string.Empty;
}
