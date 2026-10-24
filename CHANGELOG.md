# Changelog

## 2026-10-24 - MVP cleanup
- Merged the unpushed local fix (sync recovery edge cases) with origin/main (PR #1: overlay icons, thread safety, deletion pipeline). Resolved the `BuildStatus` conflict: kept the engine-list lock and the aggregated per-pair status.
- `publish.bat` now builds one self-contained `SyncIt.exe` (no .NET runtime needed).
- README: install steps, "Why SyncIt vs Google Drive for desktop", honest limits.
- `.gitignore`: release binaries (`*.exe`), local agent folders and machine-specific Claude settings.
- Stopped tracking `.claude/settings.json` and `.claude/settings.local.json` (machine-specific paths). Added `AGENTS.md`.
- Verified: Release build OK, 110/110 tests pass, gitleaks finds no secrets in the tree or history.
