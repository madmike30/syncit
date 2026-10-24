using System.Text.Json;

namespace SyncIt.Core.Helpers;

/// <summary>Parses the client_secret_*.json file Google Cloud Console downloads for an OAuth client.</summary>
public static class ClientSecretFileParser
{
    public sealed record Result(string? ClientId, string? ClientSecret, string? ProjectId, string? Error)
    {
        public bool Ok => Error is null;
    }

    public static Result ParseFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Fail("File not found.");
            if (info.Length > 64 * 1024) return Fail("That file is too big to be a Google client file.");
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) { return Fail($"Could not read the file: {ex.Message}"); }
    }

    public static Result Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Fail("This isn't a Google client file (not valid JSON). Download it again from the Clients page."); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("This isn't a Google client file.");
            if (root.TryGetProperty("web", out _))
                return Fail("This is a \"Web application\" client. SyncIt needs a \"Desktop app\" client. Create a new client and choose Desktop app.");
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "service_account")
                return Fail("This is a service account key, not an OAuth client. Create an OAuth client of type Desktop app.");
            if (!root.TryGetProperty("installed", out var inst) || inst.ValueKind != JsonValueKind.Object)
                return Fail("This file doesn't contain a Desktop app client. Download the JSON from the Clients page after creating a Desktop app client.");

            string? Str(string name) => inst.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
            var id = Str("client_id");
            var secret = Str("client_secret");
            var project = Str("project_id");

            var err = OAuthClientValidator.ValidateClientId(id) ?? OAuthClientValidator.ValidateClientSecret(secret);
            return err is null ? new Result(id, secret, project, null) : new Result(id, null, project, err);
        }
    }

    private static Result Fail(string error) => new(null, null, null, error);
}
