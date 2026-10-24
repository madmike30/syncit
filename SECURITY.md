# Security

## Reporting a vulnerability
Please do not open a public issue. Use GitHub's private
[security advisory form](https://github.com/madmike30/syncit/security/advisories/new)
for this repository. Expect a first reply within 7 days.

## How SyncIt handles credentials
- **OAuth client:** official builds include a built-in Google *Desktop app* client, injected at build time from GitHub Actions secrets and never committed. Google's guidance for installed apps says a desktop client secret is **not confidential**, because it can be extracted from any desktop app. Account security comes from your consent in the browser, not from this value. If you use your own client (optional, advanced), it's stored in Windows Credential Manager, never in a file.
- **Refresh token:** stored in `%APPDATA%\SyncIt\token.bin`, encrypted with Windows DPAPI
  (CurrentUser scope), so only your Windows account can decrypt it.
- **Sign-in:** standard Google OAuth in your browser with a localhost loopback redirect.
  SyncIt never sees your Google password.
- **Scope:** `drive` (full Drive access), needed to sync arbitrary folders. `drive.file` would hide existing folders, so it cannot be used; see [docs/SETUP-GOOGLE-OAUTH.md](docs/SETUP-GOOGLE-OAUTH.md).
- **Logs:** `%APPDATA%\SyncIt\logs`, kept 7 days. Tokens and secrets are not logged; logs contain file paths and names. **Export Logs** additionally redacts token-like strings, client secrets and email addresses; crash reports go through the same redaction.
- **Imported client files** (`client_secret_*.json`) are read once to fill in the ID and secret. SyncIt doesn't keep a copy, so you can delete the downloaded file.
- **No telemetry, no SyncIt server:** the app talks only to Google APIs.

## Release integrity
Each release includes `SHA256SUMS.txt`. Builds are produced by GitHub Actions from tagged commits; signing is enabled once a certificate is configured (see [docs/RELEASING.md](docs/RELEASING.md)).

## Supported versions
Only the latest release receives fixes.
