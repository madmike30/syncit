# SyncIt Configuration Reference

All configuration is stored in `%APPDATA%\SyncIt\syncit.json`. This file contains no secrets — OAuth credentials are stored separately in Windows Credential Manager.

See [syncit.example.json](syncit.example.json) for a complete annotated example.

## Global Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `tokenStorePath` | string | `%APPDATA%\SyncIt\token.bin` | Path to the DPAPI-encrypted OAuth token file |
| `stateDbPath` | string | `%APPDATA%\SyncIt\state.db` | Path to the SQLite baseline state database |
| `deleteJournalPath` | string | `%APPDATA%\SyncIt\delete-journal.db` | Path to the deletion audit journal database |
| `logDirectory` | string | `%APPDATA%\SyncIt\logs` | Directory for log files |
| `localTrashPath` | string | `%APPDATA%\SyncIt\.syncit-trash` | Directory for local trash (deleted files) |
| `overlayDbPath` | string | `%APPDATA%\SyncIt\overlay-state.db` | Path to the Explorer overlay state database |
| `pollIntervalSeconds` | int | `30` | How often (seconds) to poll Google Drive for remote changes |
| `maxDeletionsPerCycle` | int | `10` | If more than this many files would be deleted in one cycle, freeze all deletions and require user approval |
| `deletionGraceCycles` | int | `2` | Number of sync cycles a deletion waits before executing. At 30s poll interval, default = ~60s grace period |
| `pipeName` | string | `SyncItIpc` | Named pipe name for tray↔service IPC. Only change if you have a naming conflict |
| `syncPairs` | array | `[]` | List of sync pair configurations (see below) |

### Environment Variables

All path properties support `%APPDATA%` and other Windows environment variables. They are expanded at startup.

## Sync Pair Settings

Each entry in the `syncPairs` array defines one local↔remote folder mapping.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `id` | string | Random 8-char hex | Unique ID for this pair. **Do not change after first sync** — it's the primary key in all databases |
| `localPath` | string | *(required)* | Absolute path to the local folder to sync |
| `remoteFolderId` | string | *(required)* | Google Drive folder ID (from the folder URL) |
| `remoteFolderName` | string | `""` | Display name for the folder (not used for sync) |
| `enabled` | bool | `true` | Whether this pair is active |
| `exportGoogleDocs` | bool | `true` | Convert Google Docs/Sheets/Slides to Office formats on download |
| `conflictPolicy` | object | See below | How to resolve conflicts |
| `excludeGlobs` | string[] | See below | Glob patterns for files to exclude |
| `includeGlobs` | string[] | `[]` | Glob patterns for files to include (empty = include all) |
| `maxFileSizeBytes` | long | `5368709120` (5GB) | Skip files larger than this |
| `selectedSubfolders` | string[] | `[]` | Only sync these subfolders (empty = sync everything) |

### Finding Your Drive Folder ID

Open the folder in Google Drive on the web. The URL looks like:
```
https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz
```
The folder ID is the last segment: `1AbCdEfGhIjKlMnOpQrStUvWxYz`

## Conflict Policy

```json
"conflictPolicy": {
  "resolution": "LastWriteWins",
  "keepBothSuffix": "(SyncIt conflict {date})"
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `resolution` | string | `LastWriteWins` | Conflict resolution strategy (see table below) |
| `keepBothSuffix` | string | `(SyncIt conflict {date})` | Filename suffix for `KeepBoth` mode. `{date}` is replaced with `yyyy-MM-dd` |

### Resolution Strategies

| Value | Behavior |
|-------|----------|
| `LastWriteWins` | The newer version (by modification time) overwrites the other |
| `KeepBoth` | Both versions are kept. The remote copy gets the `keepBothSuffix` appended before its extension |
| `PreferLocal` | Local version always wins. Remote changes are discarded |
| `PreferRemote` | Remote version always wins. Local changes are discarded |

A conflict occurs when the same file is modified on both sides between sync cycles.

## File Filtering

### Exclude Globs

Files matching any pattern in `excludeGlobs` are not synced. Default excludes:

```json
"excludeGlobs": [
  "*.tmp",
  "*.~*",
  "~$*",
  "Thumbs.db",
  ".DS_Store",
  "node_modules/**",
  ".git/**",
  "desktop.ini",
  "*.syncit-tmp-*"
]
```

### Include Globs

If `includeGlobs` is non-empty, **only** files matching at least one pattern are synced (minus any excludes). Leave empty to include everything.

```json
"includeGlobs": ["*.docx", "*.pdf", "*.xlsx"]
```

### Glob Syntax

| Pattern | Matches |
|---------|---------|
| `*.tmp` | Any file ending in `.tmp` |
| `~$*` | Office lock files (e.g., `~$Report.docx`) |
| `node_modules/**` | Everything inside `node_modules/` recursively |
| `*.{jpg,png}` | Files ending in `.jpg` or `.png` |
| `Reports/*.pdf` | PDF files directly inside `Reports/` |
| `**/backup/**` | Any path containing a `backup/` segment |

Patterns are matched against the file's path relative to the sync pair's `localPath`.

## Selective Subfolder Sync

Limit which subfolders are synced from the Drive folder:

```json
"selectedSubfolders": ["Projects", "Work/Reports"]
```

- Empty array (default) = sync everything in the Drive folder
- Files at the root of the Drive folder are always synced regardless of this setting
- Paths are case-insensitive and use forward slashes
- Use the tray's **Select Folders...** dialog for interactive configuration

## Google Docs Export

When `exportGoogleDocs` is `true`, Google-native documents are exported to Office formats:

| Google Format | Local Format |
|---------------|-------------|
| Google Docs | `.docx` |
| Google Sheets | `.xlsx` |
| Google Slides | `.pptx` |
| Google Drawings | `.svg` |

Set to `false` if you don't want Google Docs downloaded at all (they have no direct file equivalent).

## Deletion Safety Settings

SyncIt has built-in safeguards against accidental mass deletions:

| Setting | Default | Purpose |
|---------|---------|---------|
| `maxDeletionsPerCycle` | `10` | Freeze threshold — if more than this many files would be deleted, freeze all deletions |
| `deletionGraceCycles` | `2` | Grace period — deletions wait this many poll cycles before executing |

### How It Works

1. A file is deleted (locally or remotely)
2. SyncIt verifies the file exists in the sync baseline (prevents phantom deletes)
3. The deletion enters a grace period (default ~60s)
4. If more than `maxDeletionsPerCycle` deletions are pending, ALL deletions are frozen
5. The tray shows a warning notification — user must approve or cancel
6. Deleted files go to `.syncit-trash/` (local) or Drive Trash (remote) — recoverable for 30 days

### Adjusting for Your Needs

- **More cautious**: Set `maxDeletionsPerCycle` to `5` and `deletionGraceCycles` to `5`
- **Less cautious**: Set `maxDeletionsPerCycle` to `50` and `deletionGraceCycles` to `1`
- **Disable freeze**: Set `maxDeletionsPerCycle` to `999999` (grace period still applies)

## Minimal Configuration

The absolute minimum configuration:

```json
{
  "syncPairs": [
    {
      "id": "my-pair",
      "localPath": "C:\\Users\\You\\MySyncFolder",
      "remoteFolderId": "YOUR_DRIVE_FOLDER_ID"
    }
  ]
}
```

Everything else uses sensible defaults. OAuth credentials are entered on first run via the setup dialog and stored in Windows Credential Manager.

## Hot Reload

SyncIt watches `syncit.json` for changes. When the file is saved:

- New sync pairs are started automatically
- Removed sync pairs are stopped
- Changed settings (poll interval, filters, etc.) take effect on the next sync cycle
- No restart required

## Log Configuration

Logging is configured via Serilog in `SyncIt.App/Program.cs`:

- Console output: `Information` level and above
- File output: `Debug` level and above, daily rolling, 30-day retention
- Log path: `%APPDATA%\SyncIt\logs\service-YYYY-MM-DD.log`
- Google APIs logging is suppressed to `Warning` level to reduce noise
