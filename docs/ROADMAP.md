# SyncIt roadmap

## Done
- **1.3.0** — multiple Google accounts at once (per-account sign-in and Disconnect), shared drives, auto-update with SHA-256 verification, Settings window.
- **1.2.0** — sync engine hardening (duplicates, lookup-before-create, settle/temp files, renames, long paths, token expiry, Unicode, rate limits).

## Next (P2)
- **Code signing** — sign releases so Windows SmartScreen stops warning (until then: More info → Run anyway, and verify with `SHA256SUMS`).
- **Explorer status icons for every folder** — today the overlay covers the first enabled folder.
- **Account per folder in the dashboard** — show the account e-mail on each folder card.
- **Google verification of the built-in sign-in** — removes the "unverified app" screen and the user cap.

## Later (P3)
- Selective sync inside shared drives (today a shared drive is synced as a whole, minus exclude patterns).
- Per-folder bandwidth limits and schedules.
