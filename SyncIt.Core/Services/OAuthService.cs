using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using SyncIt.Core.Helpers;
using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

/// <summary>
/// Manages Google OAuth2 authentication with DPAPI-encrypted token storage.
///
/// Flow:
/// 1. First run: browser opens for consent, token saved encrypted to disk.
/// 2. Subsequent runs: token loaded from disk, access token auto-refreshed by Google SDK.
/// 3. If refresh token is expired: ReauthorizeAsync() triggers a new consent flow.
///
/// NOTE: This must run as the interactive Windows user (not SYSTEM/LocalSystem).
/// The service runs as the user account; the tray app handles first-run auth.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OAuthService : IOAuthService
{
    private readonly SyncConfig _config;
    private readonly ILogger<OAuthService> _logger;
    private UserCredential? _credential;

    private static readonly string[] Scopes = { DriveService.Scope.Drive };
    private const string UserId = "syncit-user";

    public OAuthService(SyncConfig config, ILogger<OAuthService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public UserCredential? Credential => _credential;
    public bool IsAuthenticated => _credential is not null;

    public async Task<UserCredential> InitializeAsync(CancellationToken ct = default)
    {
        return await AuthorizeInternalAsync(forceAccountPicker: false, ct);
    }

    public async Task<UserCredential> ReauthorizeAsync(CancellationToken ct = default)
    {
        _credential = null;
        var dataStore = new DpapiDataStore(_config.TokenStorePath, _logger);
        await dataStore.DeleteAsync<TokenResponse>(UserId);
        return await AuthorizeInternalAsync(forceAccountPicker: true, ct);
    }

    private async Task<UserCredential> AuthorizeInternalAsync(bool forceAccountPicker, CancellationToken ct)
    {
        var client = OAuthClientResolver.ResolveForApp();
        var (clientId, clientSecret) = (client.ClientId, client.ClientSecret);
        _logger.LogInformation("Using {Source} Google OAuth client.", client.Source);
        var dataStore = new DpapiDataStore(_config.TokenStorePath, _logger);
        var clientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };

        try
        {
            if (forceAccountPicker)
            {
                // Use a custom flow with prompt=select_account so Google shows the
                // account picker instead of auto-selecting the last used account.
                // "select_account" forces account selection; "consent" alone only
                // forces re-consent but may auto-select the same account.
                var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = clientSecrets,
                    Scopes = Scopes,
                    DataStore = dataStore,
                    Prompt = "select_account",
                });

                _credential = await new Google.Apis.Auth.OAuth2.AuthorizationCodeInstalledApp(
                    flow,
                    new Google.Apis.Auth.OAuth2.LocalServerCodeReceiver())
                    .AuthorizeAsync(UserId, ct);
            }
            else
            {
                _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    clientSecrets,
                    Scopes,
                    UserId,
                    ct,
                    dataStore);
            }

            _logger.LogInformation("OAuth initialized. Token expiry: {Expiry}", _credential.Token.IssuedUtc);
            return _credential;
        }
        catch (TokenResponseException ex) when (ex.Error.Error == "invalid_grant")
        {
            _logger.LogWarning("Refresh token expired. Clearing token and re-authorizing.");
            await dataStore.DeleteAsync<TokenResponse>(UserId);
            return await AuthorizeInternalAsync(forceAccountPicker, ct);
        }
    }
}

/// <summary>
/// IDataStore implementation that encrypts token data with DPAPI before writing to disk.
/// Replaces the default FileDataStore which writes plaintext JSON.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiDataStore : IDataStore
{
    private readonly string _tokenPath;
    private readonly ILogger _logger;
    private Dictionary<string, JsonNode>? _cache;

    public DpapiDataStore(string tokenPath, ILogger logger)
    {
        _tokenPath = tokenPath;
        _logger    = logger;
    }

    public Task StoreAsync<T>(string key, T value)
    {
        LoadCache();
        var json = JsonSerializer.Serialize(value);
        _cache![key] = JsonNode.Parse(json)!;
        Persist();
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        LoadCache();
        _cache!.Remove(key);
        Persist();
        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        LoadCache();
        if (_cache!.TryGetValue(key, out var node))
        {
            var result = node.Deserialize<T>();
            return Task.FromResult(result);
        }
        return Task.FromResult(default(T));
    }

    public Task ClearAsync()
    {
        _cache = new();
        Persist();
        return Task.CompletedTask;
    }

    private void LoadCache()
    {
        if (_cache is not null) return;
        var json = CredentialHelper.LoadTokenJson(_tokenPath);
        _cache = json is null
            ? new Dictionary<string, JsonNode>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonNode>>(json) ?? new();
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_cache);
        CredentialHelper.SaveTokenJson(_tokenPath, json);
    }
}
