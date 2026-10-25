# SyncIt roadmap

## Done
- **1.4.3 / 1.4.4** — reliable settings hot-reload, quick exit on close, Explorer status-icon pass in the background (faster startup).
- **1.4.0** — offline recovery + "Offline - retrying", account colour/badge on every folder and activity row, easy Add account, pause per folder/account, active hours, conflict/deletion notifications, backup-only folders, folder deletes reach Drive, selective config reload, map a pair to any My Drive folder, version history & restore (incl. Drive trash), first-sync preview, per-folder Docs export format, USB/external-disk folders, Explorer Copy Drive link / Open in Drive, duplicate finder, encrypted backup folders (Experimental).
- **1.3.0** — multiple Google accounts at once (per-account sign-in and Disconnect), shared drives, auto-update with SHA-256 verification, Settings window.
- **1.2.0** — sync engine hardening (duplicates, lookup-before-create, settle/temp files, renames, long paths, token expiry, Unicode, rate limits).

## Next (P2)
- **Explorer status icons for every folder** — today the overlay covers the first enabled folder.
- **Google verification of the built-in sign-in** — removes the "unverified app" screen and the user cap.
- **Encrypted folders out of beta** — external review, Argon2id key format, two-way mode.

## Later (P3)
- Selective sync inside shared drives (today a shared drive is synced as a whole, minus exclude patterns).
- Per-folder bandwidth limits and schedules.

## Not planned
- Code signing (cost). The README explains the SmartScreen warning and how to verify downloads with `SHA256SUMS`.
