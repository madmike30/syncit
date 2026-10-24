using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SyncIt.Core.Services;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string SetupUrl, string SetupName, string Sha256SumsUrl);

/// <summary>
/// Checks GitHub Releases ("latest" = newest published, non-draft, non-prerelease) for a newer SyncIt,
/// downloads the installer and verifies it against the release's SHA256SUMS before it may run.
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultRepo = "madmike30/syncit";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _repo;

    public UpdateChecker(HttpClient http, ILogger logger, string repo = DefaultRepo)
    {
        _http = http;
        _logger = logger;
        _repo = repo;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SyncIt-Updater");
    }

    /// <summary>Returns the newer release, or null when up to date / nothing usable is published.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Update check: GitHub returned {Status}.", (int)resp.StatusCode);
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseLatest(json, current);
    }

    /// <summary>Parses the GitHub "latest release" JSON. Pure, unit-tested.</summary>
    public static UpdateInfo? ParseLatest(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var version) || version <= Normalize(current)) return null;

        string? setupUrl = null, setupName = null, sumsUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.Equals($"SyncIt-{version.ToString(3)}-setup.exe", StringComparison.OrdinalIgnoreCase)) { setupUrl = url; setupName = name; }
            else if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
        }
        // Never offer an update that cannot be verified.
        if (setupUrl is null || sumsUrl is null) return null;
        if (!IsTrustedUrl(setupUrl) || !IsTrustedUrl(sumsUrl)) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, page, setupUrl, setupName!, sumsUrl);
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Only download from GitHub over HTTPS.</summary>
    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the hash for <paramref name="fileName"/> in a sha256sum-style file ("hash  name" or "hash *name").</summary>
    public static string? FindHash(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64];
            var name = line[64..].Trim().TrimStart('*').Trim();
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && hash.All(Uri.IsHexDigit))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Downloads the installer into <paramref name="directory"/> and verifies its SHA-256.
    /// Throws (and deletes the file) if the hash is missing or does not match.</summary>
    public async Task<string> DownloadVerifiedAsync(UpdateInfo update, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var sums = await _http.GetStringAsync(update.Sha256SumsUrl, ct);
        var expected = FindHash(sums, update.SetupName)
            ?? throw new InvalidDataException($"SHA256SUMS has no entry for {update.SetupName}.");

        var target = Path.Combine(directory, update.SetupName);
        await using (var src = await _http.GetStreamAsync(update.SetupUrl, ct))
        await using (var dst = File.Create(target))
            await src.CopyToAsync(dst, ct);

        string actual;
        await using (var fs = File.OpenRead(target))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();

        if (actual != expected)
        {
            File.Delete(target);
            throw new InvalidDataException($"Downloaded installer failed verification (SHA-256 mismatch).");
        }
        _logger.LogInformation("Update {Version} downloaded and verified (SHA-256 OK).", update.Version);
        return target;
    }
}
