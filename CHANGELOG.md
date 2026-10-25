# Changelog

## 1.4.3 - 2026-10-25
- Fixed: settings edits made outside the app were sometimes applied minutes late or missed (including edits made while the app was starting). The watcher now debounces, retries while the file is locked, handles atomic replace, ignores touches that change nothing (content hash), and re-checks every 30 s.
- Fixed: a normal close request (installer, logoff, `taskkill` without /F) was ignored, and quitting could hang. Engines now stop off the UI thread within 4 s, and the app always exits within about 6 s.
- Faster startup: the Explorer status-icon pass over every file now runs in the background, so sync engines start without waiting for it.

## 1.4.2 - 2026-10-25
- Fixed: a folder deleted on the PC was still refused by the deletion safety check (folders have no baseline row), so the Drive folder stayed. Folder deletes are now approved once the engine has verified every file inside was synced; other untracked deletions are still rejected. Found in the live test of 1.4.1.

## 1.4.1 - 2026-10-25
- Fixed: deleting a folder on the PC did not trash the matching Drive folder in the live app (folder lookup skipped Google folder types). Same fix for Explorer "Copy Drive link" on folders. Found in a live two-account test.

## 1.4.0 - 2026-10-25
### Fixes
- **Recovers by itself after a network outage.** A Drive request that hung after the connection dropped could block the sync loop forever, so the app looked disconnected until restarted. Every metadata request attempt is now capped (90 s) and retried, the poll loop can no longer stop on an error, and failed cycles back off (poll interval doubling, up to 5 min).
- **Offline status.** When Google Drive can't be reached the tray and dashboard show **Offline - retrying**, and the log records when the connection is restored. SyncIt retries the moment the network returns or the PC wakes from sleep.
- **Deleting a folder on the PC now trashes the Drive folder too** (not just its files) - through the same grace period and mass-deletion protection, and only when every file in it on Drive is one SyncIt synced (anything that never reached this PC is kept).
- **Deleted sync root folder:** no more "FSW error … Access is denied" spam; the folder shows **Local folder missing**, sync waits, and resumes when the folder is back.
- **Config changes restart only the folders that changed.** Other folders keep running (no 40 s rescan of the main folder when a second one is added, paused or edited).
- Log shows `Accounts ready: N of N (emails)` after start, account add/remove and config reload, and each folder's start line names its account.

### New
- **Adding an account is easy to find:** **+ Add Google account** and **Manage accounts** in the dashboard header; **Accounts...** and **Add Google account...** in the tray menu; Settings → Accounts has the primary button above the list, and each account has its own **Pause** and **Disconnect…**.
- **Every folder shows its account:** colour + initial badge per account, card title `Google Drive · account`, the Drive location (`My Drive`, `My Drive / Folder`, `Shared drive: X`), an **Account** column in Recent Activity, and account badges in the tray tooltip. Same in Settings → Folders and the folder picker.
- **Pause per folder** (dashboard card) **and per account** (Settings → Accounts) - pausing never restarts other folders.
- **Sync schedule (active hours)** in Settings → General; outside the window SyncIt pauses and catches up afterwards.
- **Notifications for conflicts and deletions** (one per burst) and for mass-deletion protection, each with its own switch in Settings → General.
- **Backup only mode per folder:** PC → Drive only; nothing on the PC is ever downloaded, overwritten or deleted; conflicts keep the PC copy; optionally keep files on Drive when they are deleted on the PC (default).
- **Sync a My Drive folder directly:** when adding a folder you can type a Drive folder (e.g. `SyncIt-Test` or `Work/Docs`, created if missing) and the pair maps straight to it.
- **Max file size per folder** in Settings → Folders (exclude patterns were already there; defaults skip `node_modules`, `.git`, `*.tmp`, `~$*`).
- **Version history & restore.** Right-click a synced file in Explorer → SyncIt → **Version history…** lists its Drive versions; restoring writes the old content back (checksum-verified) after moving the current copy to the SyncIt trash. **Tools → Restore deleted files** lists items in the Drive trash that belonged to the folder and takes the ones you check out of the trash.
- **Preview before the first sync.** Adding a folder shows how many files will upload, download, already match or differ - and that nothing is deleted - before you start it.
- **Docs/Sheets/Slides export format per folder** (e.g. docx/odt/pdf, xlsx/ods/csv, pptx/odp/pdf), optional read-only copies; exported copies are never uploaded back.
- **Folders on USB / external disks** are bound to that disk's volume serial: sync pauses with "Disk not connected" when it is unplugged and resumes when the same disk returns.
- **Explorer right-click menu:** Copy Drive link, Open in Drive, Version history… (per user, removed on uninstall).
- **Duplicate finder** (Tools): same folder + same name + identical MD5. You choose what goes to the Drive trash; the oldest or PC-linked copy is protected and at least one copy always stays.
- **Encrypted backup folder - Experimental, beta** (off by default): one-way, client-side AES-256-GCM with encrypted names; see `docs/ENCRYPTION.md`.
- Fixed: the second account's folder title showed grey instead of its account colour.
## 1.3.2 - 2026-10-24
- **Errors from tray menu, dashboard links and Settings are always logged.** Actions started outside the normal window message loop (for example through Windows accessibility / UI Automation) could fail silently; they now go through the crash reporter (log + crash report + friendly message).

## 1.3.1 - 2026-10-24
- **Fixed the new Settings window not opening.** Applying the theme re-raised the page-selection event while a page was being built, which crashed the window. Pages are now built once, with proper margins and a wider window.
- README: screenshots of Settings and the add-folder account/drive picker.

## 1.3.0 - 2026-10-24
- **Multiple Google accounts at the same time.** Add accounts in Settings → Accounts; each sync folder belongs to one account and each account has its own DPAPI-encrypted sign-in and its own Disconnect (files on the PC and in Drive are never deleted). Your existing sign-in and folder are migrated automatically - no new sign-in, no re-sync.
- **Shared drives.** When you add a folder you choose the account and either My Drive or one of its shared drives.
- **Auto-update.** SyncIt checks GitHub Releases once a day, shows the update in the tray, downloads the installer, verifies it against the release's SHA256SUMS and installs only after you confirm. Turn it off in Settings → Updates.
- **New Settings window** (Windows 11 style, light by default): accounts, folders (exclude patterns, conflict rule, pause per folder), appearance, updates, start with Windows and bandwidth limits.
- Changing a folder's account or drive now resets only that folder's sync state (other folders keep theirs).
- README: honest comparison table with Google Drive for desktop and a "Windows SmartScreen warning" section.
## 1.2.0 - 2026-10-24
Sync engine hardening (review recommendations):
- **Duplicate names on Drive are detected.** Google Drive allows two files with the same name in one folder; SyncIt now warns (log + Activity) and syncs the newest one instead of picking one at random.
- **No duplicate uploads.** Before creating a new file on Drive, SyncIt checks whether a file with that name already exists in the folder. Identical content is adopted; different content goes through your conflict policy and the existing file is updated in place.
- **Files that are still being written or are locked wait.** A file must be quiet for 2 seconds and readable before upload; otherwise it is retried. Office/LibreOffice lock files, browser partial downloads (.crdownload, .part, .partial) and editor temp files are never synced.
- **Renames are renames.** Renaming or moving a synced file renames/moves it on Drive (no re-upload), including case-only renames (
otes.txt → Notes.txt) in both directions.
- **Long paths** (over 260 characters) work with Explorer status integration.
- **Expired change token recovers automatically.** If Drive reports the change token is no longer valid (e.g. after a long time offline), SyncIt takes a fresh token and runs a safe full rescan (never deletes).
- **Unicode-safe path matching.** Names with accents/Hebrew written in different Unicode forms (NFC/NFD) and case differences are matched, not duplicated.
- **Rate limits are retried with backoff.** HTTP 429 and 403 rate-limit errors (userRateLimitExceeded, ateLimitExceeded, ...) are retried with exponential backoff; real permission errors are not.
- Planned (P2): multiple Google accounts - see [docs/ROADMAP.md](docs/ROADMAP.md).
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
