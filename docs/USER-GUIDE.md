# SyncIt User Guide

SyncIt is a lightweight Google Drive sync tool for Windows. It runs as a system tray application that keeps a local folder in sync with a Google Drive folder — similar to Google Drive for Desktop, but simpler and fully under your control.

## Quick Start

### Prerequisites
- Windows 10/11
- .NET 8.0 Runtime (or SDK for development)
- A Google Cloud project with the Drive API enabled
- OAuth 2.0 credentials (Client ID + Client Secret)

### Step 1: Set Up Google Cloud Credentials

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project (or use an existing one)
3. Enable the **Google Drive API**
4. Go to **Credentials** > **Create Credentials** > **OAuth 2.0 Client ID**
5. Application type: **Desktop app**
6. Download or copy the **Client ID** and **Client Secret**

### Step 2: Configure SyncIt

Copy the example config and edit it:

```
%APPDATA%\SyncIt\syncit.json
```

See `docs/syncit.example.json` for a complete example. The key settings are:

```json
{
  "syncPairs": [
    {
      "id": "my-pair",
      "localPath": "C:\\Users\\You\\MyDriveSync",
      "remoteFolderId": "YOUR_DRIVE_FOLDER_ID",
      "remoteFolderName": "My Drive Folder",
      "enabled": true
    }
  ]
}
```

**Finding your Drive folder ID:** Open the folder in Google Drive on the web. The URL will look like:
```
https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz
```
The folder ID is the last part: `1AbCdEfGhIjKlMnOpQrStUvWxYz`

### Step 3: First Run

1. Run `SyncIt.exe` (or `dotnet run --project SyncIt.App` for development)
2. On first launch, a browser window opens for Google sign-in
3. Authorize SyncIt to access your Drive files
4. The tray icon appears — SyncIt is now running

### Step 4: You're Done

SyncIt will now:
- Upload new/changed local files to Drive
- Download new/changed Drive files to your local folder
- Handle conflicts using your configured policy (default: last write wins)
- Safely handle deletions with a grace period and threshold protection

## How It Works

### Tray Icon States

| Icon | State | Meaning |
|------|-------|---------|
| Blue cloud | Idle | Everything is synced |
| Green cloud | Syncing | Files are being transferred |
| Gray cloud | Paused | Sync is paused by user |
| Red cloud | Error | Something went wrong |
| Amber cloud | Warning | Deletion freeze — needs your attention |

### Right-Click Menu

The tray menu shows account info, recent activity, and sync controls:

| Item | Description |
|------|-------------|
| **SyncIt — user@gmail.com** | Account header with your Google email |
| **Storage bar** | Google Drive storage usage with visual progress bar |
| **Recent activity** | Last 3 file transfers (active + completed) shown inline |
| **Open SyncIT Folder** | Opens your sync folder in File Explorer |
| **Open Status...** | Shows detailed sync status, transfers, and pair details |
| **Sync Now** | Triggers an immediate sync cycle (normally every 30s) |
| **Pause / Resume Syncing** | Temporarily stops or resumes syncing |
| **Select Folders...** | Choose which Drive subfolders to sync |
| **Preferences...** | Opens `syncit.json` in your default editor |
| **Open Log Folder** | Opens the log folder for troubleshooting |
| **Start with Windows** | Toggle — adds/removes SyncIt from Windows startup |
| **Notifications** | Toggle — enable/disable balloon notifications |
| **Quit SyncIt** | Stops syncing and closes both the tray and the background service |

### Sync Direction

SyncIt is **bidirectional**:

- **Local changes** (file created, modified, renamed, deleted) are detected instantly via FileSystemWatcher and uploaded to Drive
- **Remote changes** (files added/modified/deleted on Drive) are detected by polling the Drive Changes API every 30 seconds (configurable)

### Conflict Resolution

When the same file is changed on both sides between syncs, SyncIt resolves the conflict based on your policy:

| Policy | Behavior |
|--------|----------|
| `LastWriteWins` | The newer version (by modification time) wins |
| `KeepBoth` | Both versions are kept; remote gets a "(SyncIt conflict)" suffix |
| `PreferLocal` | Local version always wins |
| `PreferRemote` | Remote version always wins |

### Deletion Safety

SyncIt has a three-layer safety system to prevent accidental mass deletions:

1. **Baseline verification** — Only files that exist in the sync baseline can be deleted
2. **Grace period** — Deletions wait 2 sync cycles (~60s) before executing, giving you time to undo
3. **Threshold freeze** — If more than 10 files would be deleted at once, ALL deletions are frozen and the tray shows a warning. You must explicitly approve or cancel them

Deleted files are moved to `%APPDATA%\SyncIt\.syncit-trash\` (local) or Google Drive's Trash (remote), so they're recoverable for 30 days.

## Large File Handling

SyncIt handles large files efficiently:

- Files over 5MB are streamed directly to disk during download (no memory buffering)
- Google Drive's resumable upload protocol is used automatically for large files
- Per-file transfer progress is tracked and shown in the Status window
- Progress updates are pushed to the tray at 1Hz for active transfers

## Explorer Sync Status Column

SyncIt shows sync status in Explorer's **Status column** — the same modern approach used by OneDrive. This uses the Windows Cloud Files API and requires no admin privileges or COM registration.

### How It Works

When the service starts, it registers your sync folder as a cloud sync root. Explorer then shows a Status column with sync icons:

| Status | Icon | Meaning |
|--------|------|---------|
| In-sync | Green checkmark | File/folder is up to date |
| Not-in-sync | Blue sync arrows | Transfer in progress or pending |

### Enabling the Status Column

The Status column should appear automatically in your sync folder. If it doesn't:
1. Open your sync folder in Explorer
2. Right-click the column header
3. Click "More..."
4. Check "Status" and click OK

### Folder Status

Folder status is automatically derived from its contents:
- If all files/subfolders under a folder are synced, the folder shows as synced
- If any file is syncing/pending, the parent folders show as not-synced
- Empty folders show as synced

## File Filtering

Control which files are synced using glob patterns in the sync pair config:

```json
{
  "excludeGlobs": [
    "*.tmp",
    "~$*",
    "node_modules/**",
    ".git/**"
  ],
  "includeGlobs": []
}
```

- `excludeGlobs` — Files matching any pattern are NOT synced
- `includeGlobs` — If non-empty, ONLY matching files are synced
- Files are excluded by default: `.tmp`, `~$*` (Office lock files), `Thumbs.db`, `.DS_Store`, `desktop.ini`

## Selective Subfolder Sync

You can sync only specific subfolders from your Drive folder:

1. Right-click tray icon > **Select Folders...**
2. Check the subfolders you want to sync
3. Click OK — the config is saved and the service hot-reloads

Or edit `syncit.json` directly:
```json
{
  "selectedSubfolders": ["Projects", "Work/Reports"]
}
```

## Google Docs Export

When `exportGoogleDocs` is `true` (default), Google Docs/Sheets/Slides are automatically exported to local Office formats:

| Google Format | Local Format |
|---------------|-------------|
| Google Docs | `.docx` |
| Google Sheets | `.xlsx` |
| Google Slides | `.pptx` |
| Google Drawings | `.svg` |

## Troubleshooting

### SyncIt won't start
- Check that `%APPDATA%\SyncIt\syncit.json` exists and is valid JSON
- Check that your Google OAuth credentials are set up (run with `--auth-only` flag)
- Check logs at `%APPDATA%\SyncIt\logs\`

### Files aren't syncing
- Check the tray icon — is it in Error state?
- Open Status to see pending changes and errors
- Check that the file isn't excluded by glob patterns
- Check that the file is under the configured `maxFileSizeBytes` limit (default 5GB)

### "Deletion Frozen" warning
- This means more than 10 files would be deleted at once
- Click the balloon notification to review and approve/cancel
- This is a safety feature to prevent accidental mass deletions

### Token expired
- The tray will show an "Auth Required" notification
- Click it to re-authorize, or restart SyncIt

### Logs
Service logs: `%APPDATA%\SyncIt\logs\service-YYYY-MM-DD.log`
Tray logs: `%APPDATA%\SyncIt\logs\tray-YYYY-MM-DD.log`
