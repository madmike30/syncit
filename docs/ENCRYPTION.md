# Encrypted backup folders (Experimental, beta)

Turn on **Settings → General → Experimental**, then use the tray menu **Tools → Add encrypted backup folder (beta)…**.

## What it does
- One-way backup: a folder on this PC → a Drive folder (default `My Drive / SyncIt Encrypted / <name>`).
- Files **and names** are encrypted on your PC before upload. Google (or anyone with access to the Drive folder) sees only random-looking names and data.
- **Never changes or deletes anything on your PC.** Never deletes on Drive: a changed file replaces its own encrypted copy (Drive keeps older versions), files deleted on the PC stay in the backup.
- Restore: **Tools → Restore encrypted backup (beta)…** into an **empty** folder (SyncIt never overwrites when restoring).

## Scheme (format `SYNCITE1`)
| Part | How |
|---|---|
| Key | PBKDF2-HMAC-SHA256(passphrase, random 16-byte salt, 600 000 iterations) → 64 bytes: 32 for content key-wrapping, 32 for names. Only the salt and an HMAC verifier are stored in `syncit.json`; the key never is. |
| Passphrase | Kept on this PC with Windows DPAPI (current user) so backups run unattended. |
| File content | Random 256-bit data key per file, wrapped with AES-256-GCM. Data in 64 KiB chunks, each AES-256-GCM; nonce = random 8-byte file prefix + chunk index; AAD = header + index + "last chunk" flag, so reordering, dropping or truncating chunks is detected. |
| Names | Deterministic, authenticated: nonce = HMAC-SHA256(nameKey, name)[0..12], AES-256-GCM, base32 (lowercase, Windows-safe). Same name → same encrypted name (needed to update in place). |

PBKDF2 is used because it is built into .NET (no extra native dependency); 600 000 iterations follows current OWASP guidance for PBKDF2-SHA256. Argon2id may come in a later format version.

## Limits (why it is beta)
- Equal names in the same folder produce equal encrypted names (reveals that two folders contain an equally named file, not the name).
- File sizes are visible (plus ~16 bytes per 64 KiB).
- **Lose the passphrase = lose the backup.** There is no recovery.
- Not reviewed by an external auditor yet. Keep another copy of important data.

Tests: `SyncIt.Tests/Services/V14ToolsTests.cs` (round trips of every size boundary, tamper/truncate/reorder/wrong-passphrase detection, never-delete and restore-into-empty-folder rules).
