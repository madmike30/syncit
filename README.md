# SyncIt

A lightweight, bidirectional Google Drive sync tool for Windows. Keep a local folder in sync with Google Drive — simple, reliable, and fully under your control.

## Features

- **Bidirectional sync** — local changes upload instantly via FileSystemWatcher; remote changes download via periodic polling
- **Large file streaming** — files >5 MB stream directly to disk with resumable uploads and progress tracking
- **Conflict resolution** — four configurable policies: LastWriteWins, KeepBoth, PreferLocal, PreferRemote
- **Deletion safety** — three-layer protection with baseline verification, grace period, and threshold freeze before any file is removed
- **Explorer integration** — sync status icons (✓ synced / ↻ syncing) in Windows Explorer via the Cloud Files API
- **Selective sync** — choose specific Drive subfolders; filter files with glob-based include/exclude patterns
- **Google Docs export** — auto-convert Docs → .docx, Sheets → .xlsx, Slides → .pptx, Drawings → .svg
- **System tray UI** — icon states, account info, storage bar, recent activity, and detailed status window
- **Secure credentials** — OAuth2 tokens encrypted with DPAPI; client secrets stored in Windows Credential Manager

## Requirements

- Windows 10 or 11
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK for development)

## Quick start

### Development

```bash
run-dev.bat          # builds Debug, launches SyncIt.Tray.exe
```

Or with the .NET CLI:

```bash
dotnet run --project SyncIt.Tray
```

### Release build

```bash
publish.bat          # publishes Release binaries to ./publish/
```

### First run

1. Run `SyncIt.Tray.exe` — the tray app launches the background service automatically
2. Sign in with Google when the browser opens
3. Choose your local sync folder and Drive folder
4. The tray icon appears — syncing begins

## Architecture

```
SyncIt.sln
├── SyncIt.Core/        Core library — models, services, safety, IPC
├── SyncIt.Service/     Background service (hidden, managed by tray)
└── SyncIt.Tray/        WinForms system tray application
```

The tray app runs the service as a child process. They communicate over a named pipe (`SyncItIpc`) using JSON messages. Each sync pair gets its own `SyncEngine` instance with up to 3 parallel transfers.

## Configuration

All data lives under `%APPDATA%\SyncIt\`:

| File | Purpose |
|------|---------|
| `syncit.json` | Sync pairs and settings (hot-reloads on save) |
| `token.bin` | DPAPI-encrypted OAuth2 refresh token |
| `state.db` | SQLite file baseline state |
| `delete-journal.db` | Deletion audit log |
| `logs/` | Rolling logs (30-day retention) |

See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full config reference, or [docs/syncit.example.json](docs/syncit.example.json) for an annotated example.

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — components, data flow, design decisions
- [User Guide](docs/USER-GUIDE.md) — quickstart, menu walkthrough, troubleshooting
- [Configuration](docs/CONFIGURATION.md) — complete settings reference
- [Test Plan](docs/TEST-PLAN.md) — QA test matrix

## Tech stack

- **C# / .NET 8.0** — Windows desktop (WinForms for tray UI)
- **Google.Apis.Drive.v3** — Drive API client
- **Microsoft.Data.Sqlite** — file state storage (WAL mode)
- **Polly** — retry policies for resilient API calls
- **Serilog** — structured logging
- **Windows Cloud Files API** — Explorer status column integration

## License

All rights reserved.
