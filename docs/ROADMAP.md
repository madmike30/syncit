# SyncIt roadmap

## Done
- **1.4.0** — offline recovery + "Offline - retrying", account colour/badge on every folder and activity row, easy Add account, pause per folder/account, active hours, conflict/deletion notifications, backup-only folders, folder deletes reach Drive, selective config reload, map a pair to any My Drive folder.
- **1.3.0** — multiple Google accounts at once (per-account sign-in and Disconnect), shared drives, auto-update with SHA-256 verification, Settings window.
- **1.2.0** — sync engine hardening (duplicates, lookup-before-create, settle/temp files, renames, long paths, token expiry, Unicode, rate limits).

## Next (P2)
- **Code signing** — sign releases so Windows SmartScreen stops warning (until then: More info → Run anyway, and verify with `SHA256SUMS`).
- **Explorer status icons for every folder** — today the overlay covers the first enabled folder.
- **Google verification of the built-in sign-in** — removes the "unverified app" screen and the user cap.

## Requested for a following release (not in 1.4.0)
- Version history & restore (Drive revisions, restore from Drive trash) from Explorer and the app.
- Dry-run preview (counts + list of uploads/downloads/deletes) before a new folder's first sync.
- Google Docs export options per pair (format choice, read-only copies; export itself already exists).
- Removable-drive folders tied to a volume serial (start on connect, pause on removal).
- Client-side encrypted folder (AES-GCM, Argon2id key, DPAPI-stored passphrase) — will ship behind an Experimental switch.
- Explorer context menu: Copy Drive link / Open in Drive.
- Duplicate finder per account (md5 + same folder/name), user picks what to trash.

## Later (P3)
- Selective sync inside shared drives (today a shared drive is synced as a whole, minus exclude patterns).
- Per-folder bandwidth limits and schedules.
