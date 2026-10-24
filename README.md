# SyncIt

A lightweight, bidirectional Google Drive sync tool for Windows. Keep a local folder in sync with Google Drive — simple, reliable, and fully under your control.

<p align="center"><img src="docs/images/app-icon.png" width="96" alt="SyncIt icon"></p>

| Light | Dark |
|---|---|
| ![Dashboard](docs/images/dashboard-light.png) | ![Dashboard dark](docs/images/dashboard-dark.png) |
| ![Tray menu](docs/images/tray-menu-light.png) | ![Tray menu dark](docs/images/tray-menu-dark.png) |

## Features

- **Bidirectional sync** — local changes upload instantly via FileSystemWatcher; remote changes download via periodic polling
- **Large file streaming** — files >5 MB stream directly to disk with resumable uploads and progress tracking
- **Conflict resolution** — four configurable policies: LastWriteWins, KeepBoth, PreferLocal, PreferRemote
- **Deletion safety** — three-layer protection with baseline verification, grace period, and threshold freeze before any file is removed
- **Explorer integration** — sync status icons (✓ synced / ↻ syncing) in Windows Explorer via the Cloud Files API
- **Selective sync** — choose specific Drive subfolders; filter files with glob-based include/exclude patterns
- **Google Docs export** — auto-convert Docs → .docx, Sheets → .xlsx, Slides → .pptx, Drawings → .svg
- **Windows 11 look** — Fluent-style windows and tray menu, light/dark that follows Windows, your accent color, rounded corners, crisp high-DPI icons
- **Support tools** — tray menu: About (version), Open Logs Folder, Export Logs (zip with tokens/secrets/emails redacted); crash reports written to the logs folder
- **System tray UI** — icon states, account info, storage bar, recent activity, and detailed status window
- **Secure credentials** — OAuth2 tokens encrypted with DPAPI; client secrets stored in Windows Credential Manager

## Why SyncIt vs Google Drive for desktop

Google Drive for desktop is the right choice for most people. SyncIt is for a narrower case:

- **Any Drive folder <-> any local folder.** Each sync pair links one Drive folder (by ID) to a local folder you pick. Drive for desktop mirrors your *whole* My Drive into one location (or streams it as a virtual drive).
- **Several independent sync pairs**, each with its own include/exclude globs, max file size, conflict policy and selected subfolders.
- **Plain local files, always offline.** No virtual drive and no on-demand placeholders: files are real copies on disk.
- **Explicit conflict policies:** LastWriteWins, KeepBoth, PreferLocal or PreferRemote, per pair.
- **Deletion safety:** grace period, a per-cycle deletion threshold that freezes deletes until you approve, a local trash folder and a deletion journal.
- **Google Docs/Sheets/Slides exported to .docx/.xlsx/.pptx** on download, so they open offline in Office.
- **Portable, no installer:** one self-contained `SyncIt.exe`; config is a readable JSON file that hot-reloads.
- **No SyncIt server:** you sign in with Google directly, and files go straight between your PC and Google. Optionally, technical users can bring their own OAuth client.

Limits to know (honest comparison):

- SyncIt uses **one Google account at a time** (you can switch accounts from the tray). Drive for desktop supports up to 4 accounts at once ([Google Help](https://support.google.com/drive/answer/10838124)).
- Windows only. No streaming/on-demand mode, no shared-drive or Google Photos features.
- The built-in sign-in isn't verified by Google yet, so Google shows an "unverified app" screen and limits new users. The optional own-client setup avoids both.

## Install

1. Download `SyncIt-<version>-setup.exe` (installer, no admin needed) or the portable zip from [Releases](https://github.com/madmike30/syncit/releases), or build it with `publish.bat`.
2. Put it anywhere and double-click it. No .NET install needed.
3. Click **Sign in with Google**, approve in your browser, and pick the local and Drive folders. See [docs/SETUP-GOOGLE-OAUTH.md](docs/SETUP-GOOGLE-OAUTH.md). Technical users can optionally use their own Google client (Advanced link).
## Requirements

- Windows 10 or 11
- Release: no .NET installation required
- Development only: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Quick start

### Development

```bash
dotnet run --project SyncIt.App
```

### Release build

```bash
publish.bat          # creates the standalone ./publish/SyncIt.exe (~100 MB, compressed)
```

### First run

1. Double-click `SyncIt.exe`
2. Click **Sign in with Google**
3. Approve access in the browser
4. Choose your local sync folder and Drive folder
5. The tray icon appears — syncing begins

## Architecture

```
SyncIt.sln
├── SyncIt.Core/        Core library — models, services, safety
├── SyncIt.App/         Unified single-process app (sync engine + tray icon)
└── SyncIt.Tests/       xUnit test suite (157 tests)
```

Single-process architecture like OneDrive / Google Drive: sync engines run on background threads, tray icon on the WinForms UI thread. No child processes, no IPC.

## Configuration

All data lives under `%APPDATA%\SyncIt\`:

| File | Purpose |
|------|---------|
| `syncit.json` | Sync pairs and settings (hot-reloads on save) |
| `token.bin` | DPAPI-encrypted OAuth2 refresh token |
| `state.db` | SQLite file baseline state |
| `delete-journal.db` | Deletion audit log |
| `logs/` | Rolling logs (7-day retention) |

See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full config reference, or [docs/syncit.example.json](docs/syncit.example.json) for an annotated example.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — components, data flow, design decisions
- [User Guide](docs/USER-GUIDE.md) — quickstart, menu walkthrough, troubleshooting
- [Configuration](docs/CONFIGURATION.md) — complete settings reference
- [Test Plan](docs/TEST-PLAN.md) — QA test matrix
- [Google OAuth setup](docs/SETUP-GOOGLE-OAUTH.md) — sign-in, plus optional own-client setup
- [Maintainer: Google Cloud setup](docs/GOOGLE-CLOUD-SETUP-FOR-MAINTAINER.md) — built-in client, limits, verification
- [Releasing](docs/RELEASING.md) — tags, draft releases, signing options
- [Privacy Policy](docs/PRIVACY.md) · [Terms of Use](docs/TERMS.md)
- [Security](SECURITY.md) — credential storage and reporting issues

## Tech stack

- **C# / .NET 8.0** — Windows desktop (WinForms for tray UI)
- **Google.Apis.Drive.v3** — Drive API client
- **Microsoft.Data.Sqlite** — file state storage (WAL mode)
- **Polly** — retry policies for resilient API calls
- **Serilog** — structured logging
- **Windows Cloud Files API** — Explorer status column integration

## License

[MIT](LICENSE) - Copyright (c) 2026 SyncIt contributors
