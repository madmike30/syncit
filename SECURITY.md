# Security

## Reporting a vulnerability
Please do not open a public issue. Use GitHub's private
[security advisory form](https://github.com/madmike30/syncit/security/advisories/new)
for this repository. Expect a first reply within 7 days.

## How SyncIt handles credentials
- **OAuth client (Client ID/Secret):** you create your own in Google Cloud. SyncIt stores it in
  Windows Credential Manager, never in a file or in this repo.
- **Refresh token:** stored in `%APPDATA%\SyncIt\token.bin`, encrypted with Windows DPAPI
  (CurrentUser scope), so only your Windows account can decrypt it.
- **Sign-in:** standard Google OAuth in your browser with a localhost loopback redirect.
  SyncIt never sees your Google password.
- **Scope:** `drive` (full Drive access), needed to sync arbitrary folders. A narrower scope is on the roadmap.
- **Logs:** `%APPDATA%\SyncIt\logs`, kept 7 days. Tokens and secrets are not logged; logs contain file paths and names.
- **No telemetry, no SyncIt server:** the app talks only to Google APIs.

## Supported versions
Only the latest release receives fixes.
