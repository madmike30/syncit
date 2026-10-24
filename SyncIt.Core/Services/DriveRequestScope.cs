using Google.Apis.Drive.v3;

namespace SyncIt.Core.Services;

/// <summary>A shared drive the signed-in account can see.</summary>
public sealed record SharedDriveInfo(string Id, string Name);

/// <summary>
/// Makes every Drive request work for both My Drive and shared drives.
/// All requests set supportsAllDrives; listings and changes are scoped to the shared drive when one is given.
/// </summary>
public static class DriveRequestScope
{
    public static void Apply(FilesResource.ListRequest r, string? sharedDriveId)
    {
        r.SupportsAllDrives = true;
        if (sharedDriveId is null)
        {
            r.IncludeItemsFromAllDrives = false;
            r.Corpora = "user";
        }
        else
        {
            r.IncludeItemsFromAllDrives = true;
            r.Corpora = "drive";
            r.DriveId = sharedDriveId;
        }
    }

    public static void Apply(ChangesResource.ListRequest r, string? sharedDriveId)
    {
        r.SupportsAllDrives = true;
        r.IncludeItemsFromAllDrives = sharedDriveId is not null;
        if (sharedDriveId is not null) r.DriveId = sharedDriveId;
    }

    public static void Apply(ChangesResource.GetStartPageTokenRequest r, string? sharedDriveId)
    {
        r.SupportsAllDrives = true;
        if (sharedDriveId is not null) r.DriveId = sharedDriveId;
    }

    public static void Apply(FilesResource.GetRequest r, string? _) => r.SupportsAllDrives = true;
    public static void Apply(FilesResource.CreateRequest r, string? _) => r.SupportsAllDrives = true;
    public static void Apply(FilesResource.CreateMediaUpload r, string? _) => r.SupportsAllDrives = true;
    public static void Apply(FilesResource.UpdateRequest r, string? _) => r.SupportsAllDrives = true;
    public static void Apply(FilesResource.UpdateMediaUpload r, string? _) => r.SupportsAllDrives = true;
}
