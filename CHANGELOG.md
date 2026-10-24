# Changelog

## 1.1.3 - 2026-10-24
- **Fixed one-click sign-in falling back to the own-client page.** The built-in client ID baked into release builds had a trailing newline (from how the build secret was set), so the app treated it as invalid. Values are now trimmed, the release check is strict, and the secrets were re-set without whitespace.

## 1.1.2 - 2026-10-24
- **Fixed a crash when the sign-in window opened** ("unhandled exception" dialog). The app's UI thread was not STA (async top-level `Main`), so enabling drag-and-drop on the sign-in window threw. `Main` is now `[STAThread]`, drag-and-drop is only enabled on STA threads, and background code always shows windows on the UI thread.
- Unexpected errors now show a short, friendly message (and write a crash report) instead of the raw WinForms dialog.

## 1.1.1 - 2026-10-24
- **Fixed two engines syncing the same folder.** Switching account restarted the sync engines itself while the config-file watcher restarted them a second time, so one folder ended up with two engines (and could get duplicate uploads). Engine start/stop now goes through one lifecycle gate, every start first stops what is running, the watcher is muted during account operations, and duplicate pairs (same ID or same folder) are ignored.
- **Fixed duplicate files on Drive after a first-sync conflict.** When a file existed on both sides with different content and the local copy won, SyncIt created a second file with the same name on Drive. It now updates the existing Drive file in place.
- **New: Disconnect & remove account** (Account window). After a confirmation it stops syncing, revokes SyncIt's Google access, and removes the saved sign-in, the custom client (if any), sync state, Explorer status registration and folder pairs. Your files on the PC and in Google Drive are not touched. SyncIt then shows the sign-in screen again.
- **New: Add a folder to sync** from the dashboard. The new pair stays off until you have chosen folders and confirmed, and files already on both sides are matched instead of copied again.
- When not signed in, SyncIt shows the sign-in window instead of opening the browser by itself. A missing `syncit.json` is created with defaults.
- **No more dashboard flicker:** the 3-second refresh only updates what changed, old cards are disposed, and the window paints off-screen.
- **Light theme by default.** Preferences > Appearance: Light, Dark or System (saved in `tray-settings.json`).
- Tests for the duplicate-engine fix, disconnect, and first-sync adoption (identical trees, same content, conflicts).

## 1.1.0 - 2026-10-24
- Added `docs/PRIVACY.md` and `docs/TERMS.md` (also used on Google's OAuth Branding page).
- **Windows 11 (Fluent) design:** new design system (`Theme.cs`). Segoe UI Variable, a role-based color palette, light/dark following the Windows app theme, the Windows accent color, dark title bars, rounded window corners and rounded buttons/cards. Restyled tray menu with roomy items, rounded hover highlight and its own checkmark. New app icon and DPI-aware multi-size tray status icons. Modern Inno Setup wizard with branded images. Mica isn't applied to window content, because WinForms can't draw a transparent client area.
- Fixed content touching the left window edge (it looked clipped) in the Account, Preferences, Drive folder picker and Status windows, plus tray menu text drawn about 3px from the edge.
- **One-click sign-in:** the setup screen now shows only a big **Sign in with Google** button, a plain-language note on what access SyncIt asks for and why, and help for Google's *unverified app* screen. It uses a built-in Desktop client injected at build time from `SYNCIT_GOOGLE_CLIENT_ID` and `SYNCIT_GOOGLE_CLIENT_SECRET` (GitHub secrets or env vars; never committed). Builds without it open the own-client setup.
- **Optional own-client setup (technical users):** reached from a small Advanced link or a sign-in error. Numbered steps, each with an **Open this page** button that links to the right Google Cloud page; **Import client file** plus drag-and-drop for `client_secret_*.json` (Web and service-account files are rejected); manual paste.
- Sign-in errors are now in plain language. User-cap, blocked and invalid-client errors offer the own-client option.
- `scripts/setup-google-client.ps1`: if gcloud is installed, creates the project and enables the Drive API, then opens the remaining pages.
- Docs: illustrated SETUP-GOOGLE-OAUTH.md, new GOOGLE-CLOUD-SETUP-FOR-MAINTAINER.md, SECURITY note on desktop client secrets.
- 29 new tests (157 total).

## 1.0.0 - 2026-10-24
- MIT license.
- Versioning from `Directory.Build.props` (1.0.0); version shown in tray **About** and logged at startup.
- OAuth onboarding: Client ID/Secret format validation on the setup screen; new [docs/SETUP-GOOGLE-OAUTH.md](docs/SETUP-GOOGLE-OAUTH.md). Kept the full `drive` scope (`drive.file` would hide existing folders); documented why.
- Support: tray **Open Logs Folder** and **Export Logs (zip)** with redaction of tokens, secrets and emails; global crash handler writes `crash-*.log`.
- Packaging: Inno Setup per-user installer, portable zip, SHA256SUMS; single-file exe now compressed (262 MB to ~100 MB).
- Release workflow on `v*` tags creates a **draft** GitHub Release; optional code signing via repo secrets (skipped if absent).
- 18 new tests (128 total).

## 2026-10-24 - Security tidy
- Removed third-party `.claude/commands` and a stale `.vscode` setting; deleted merged branch `claude/gracious-clarke`.
- Fixed all build warnings (nullable Drive IDs, unused variable, Drive.v3 package version).
- Added SECURITY.md and a GitHub Actions CI workflow (build + test on windows-latest).
- Verified: 0 warnings, 110/110 tests, gitleaks clean.

## 2026-10-24 - MVP cleanup
- Merged the unpushed local fix (sync recovery edge cases) with origin/main (PR #1: overlay icons, thread safety, deletion pipeline). Resolved the `BuildStatus` conflict: kept the engine-list lock and the aggregated per-pair status.
- `publish.bat` now builds one self-contained `SyncIt.exe` (no .NET runtime needed).
- README: install steps, "Why SyncIt vs Google Drive for desktop", honest limits.
- `.gitignore`: release binaries (`*.exe`), local agent folders and machine-specific Claude settings.
- Stopped tracking `.claude/settings.json` and `.claude/settings.local.json` (machine-specific paths). Added `AGENTS.md`.
- Verified: Release build OK, 110/110 tests pass, gitleaks finds no secrets in the tree or history.
