namespace SyncIt.Core.Helpers;

public enum OAuthFailureKind { AccessDenied, UserCapReached, BlockedByAdmin, InvalidClient, Other }

/// <summary>Turns Google sign-in failures into plain-language guidance.</summary>
public static class OAuthErrorClassifier
{
    public static OAuthFailureKind Classify(Exception ex)
    {
        var text = string.Join(" | ", Chain(ex).Select(e =>
            e is Google.Apis.Auth.OAuth2.Responses.TokenResponseException t
                ? $"{t.Error?.Error} {t.Error?.ErrorDescription} {t.Message}"
                : e.Message)).ToLowerInvariant();
        return ClassifyText(text);
    }

    public static OAuthFailureKind ClassifyText(string text)
    {
        text = text.ToLowerInvariant();
        if (text.Contains("user cap") || text.Contains("user_cap") || text.Contains("reached its user") || text.Contains("has not completed the google verification"))
            return OAuthFailureKind.UserCapReached;
        if (text.Contains("admin_policy_enforced") || text.Contains("org_internal") || text.Contains("disallowed_useragent") || text.Contains("blocked by your administrator"))
            return OAuthFailureKind.BlockedByAdmin;
        if (text.Contains("invalid_client") || text.Contains("unauthorized_client") || text.Contains("deleted_client") || text.Contains("redirect_uri_mismatch"))
            return OAuthFailureKind.InvalidClient;
        if (text.Contains("access_denied"))
            return OAuthFailureKind.AccessDenied;
        return OAuthFailureKind.Other;
    }

    /// <summary>True when switching to the user's own Google client is a sensible fix.</summary>
    public static bool SuggestOwnClient(OAuthFailureKind kind) =>
        kind is OAuthFailureKind.UserCapReached or OAuthFailureKind.AccessDenied or OAuthFailureKind.InvalidClient;

    public static string Message(OAuthFailureKind kind, bool usingBuiltIn) => kind switch
    {
        OAuthFailureKind.UserCapReached =>
            "Google is limiting new sign-ins for SyncIt's shared sign-in (the app is still waiting for Google's review). " +
            "You can still use SyncIt by creating your own free Google sign-in client. It takes about 10 minutes and the guide walks you through it.",
        OAuthFailureKind.AccessDenied when usingBuiltIn =>
            "Google didn't give SyncIt access. If you clicked Cancel, just try again. " +
            "If Google said the app is blocked or has reached its limit, use your own Google client instead.",
        OAuthFailureKind.AccessDenied =>
            "Google didn't give SyncIt access. Make sure your Google account is listed under Test users in your Google Cloud project, then try again.",
        OAuthFailureKind.BlockedByAdmin =>
            "This Google account is managed by an organization that blocks apps like SyncIt. Try a personal Google account, or ask your administrator.",
        OAuthFailureKind.InvalidClient when usingBuiltIn =>
            "SyncIt's built-in sign-in isn't working right now. You can use your own Google client instead.",
        OAuthFailureKind.InvalidClient =>
            "Google didn't accept your Client ID/Secret. Make sure you created a \"Desktop app\" client and pasted both values, or import the downloaded JSON file.",
        _ => "Sign-in didn't complete. Check your internet connection and try again.",
    };

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e;
        if (ex is AggregateException agg) foreach (var inner in agg.Flatten().InnerExceptions) yield return inner;
    }
}
