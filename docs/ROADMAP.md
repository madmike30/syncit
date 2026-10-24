# SyncIt roadmap

## P2 — Multiple Google accounts (planned, not implemented)

Today SyncIt signs in to one Google account and every sync pair uses it.

Plan:
1. **Per-account token store** – key `token.bin` by account id (`tokens/<sub>.bin`), migrate the current token to the signed-in account on first run.
2. **`SyncPair.AccountId`** – new optional field; existing pairs get the current account on load.
3. **Drive service per account** – `GoogleDriveService` instances cached by account; `SyncEngine` already receives its service via the constructor, so the engine itself needs no changes.
4. **UI** – *Accounts* page (add / sign out / remove); account picker in *Add folder*; account shown per pair on the dashboard.
5. **Explorer** – one cloud sync root per pair already; register with the account e-mail in the display name.
6. **Safety** – signing out of an account pauses (never deletes) its pairs; removing an account requires removing its pairs first.

Tests: token migration, pair→account resolution, sign-out pauses only that account's engines.
