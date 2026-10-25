# SyncIt Privacy Policy

_Last updated: 24 October 2026_

SyncIt is a free, open-source Windows app that keeps a folder on your PC in sync with a folder in your Google Drive. This policy explains what happens with your data. Short version: **your data stays between your PC and your Google account. We don't collect anything.**

## Who we are
SyncIt is maintained by madmike30 as an open-source project: <https://github.com/madmike30/syncit>. There is no company or server behind it.

## What SyncIt accesses
When you click **Sign in with Google** and approve, SyncIt gets permission (the Google Drive scope `https://www.googleapis.com/auth/drive`) to:
- list, read, download, upload, update and delete files in your Google Drive;
- read your account email and storage usage, to show them in the app.

SyncIt uses this only to sync the Drive folder(s) you choose with the local folder(s) you choose, and to show sync status. It doesn't use your Drive data for anything else.

## Where your data goes
- File contents and metadata move **only between your PC and Google's servers**, directly over HTTPS through Google's official APIs.
- SyncIt has **no server of its own**. Nothing is sent to the maintainer or to any third party.
- **No telemetry, analytics, ads or tracking.**

## What is stored on your PC
Everything is stored locally in `%APPDATA%\SyncIt\` under your Windows account:
- your sign-in token (`token.bin`), encrypted with Windows DPAPI so only your Windows user can decrypt it;
- if you use your own Google client (advanced): its Client ID and Secret, in Windows Credential Manager;
- sync state databases (file names, sizes, timestamps and Drive file IDs), settings, and logs (kept 7 days). Logs contain file paths and names, but no tokens.

Nothing leaves your PC unless you choose to share it yourself, for example by attaching an exported log zip to a bug report. Exported logs redact tokens, secrets and email addresses.

## Google API Services User Data Policy
SyncIt's use and transfer of information received from Google APIs follows the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements. Google user data isn't sold, used for advertising, or read by humans.

## Your control
- **Remove access** anytime at <https://myaccount.google.com/permissions>, or use **Switch Account** in SyncIt.
- **Delete local data** by quitting SyncIt and deleting `%APPDATA%\SyncIt\`. Uninstalling keeps this folder on purpose, so your sync state survives a reinstall.
- Your files in Google Drive and in your local folder stay yours. SyncIt never keeps copies elsewhere.

## Children
SyncIt isn't directed at children under 13 and collects no personal information from anyone.

## Changes
Changes to this policy are published in this file, and its history is visible on GitHub.

## Contact
Open an issue: <https://github.com/madmike30/syncit/issues>. For security problems, see [SECURITY.md](../SECURITY.md).
