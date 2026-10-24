# Releasing SyncIt

1. Update `CHANGELOG.md`, bump `<Version>` in `Directory.Build.props`.
2. Merge to `main`, then tag: `git tag v1.0.0 && git push origin v1.0.0`.
3. The **Release** workflow tests, publishes a single-file `SyncIt.exe`, builds the Inno Setup installer, a portable zip and `SHA256SUMS.txt`, and creates a **draft** GitHub Release. Review it and click Publish.

Local build: `publish.bat`, then `iscc /DAppVersion=1.0.0 installer\SyncIt.iss` (output in `dist\`).

## Code signing (optional)
The workflow signs the exe and installer only if repository secrets `SIGN_CERT_PFX_BASE64` (base64 of a .pfx) and `SIGN_CERT_PASSWORD` exist; otherwise it skips signing. Unsigned builds work but Windows SmartScreen shows "unknown publisher" until the file gains reputation.

Options (approximate prices, check current offers):
| Option | Cost | Notes |
|---|---|---|
| Unsigned | free | SmartScreen warning; fine for early adopters. |
| [SignPath Foundation](https://signpath.org/) | free for OSS | Requires a public OSS repo and approval; signing runs through their service (would replace `sign.ps1`). |
| Azure Trusted Signing | ~$10/month | Cheapest commercial option; identity validation required; uses its own GitHub action. |
| OV code-signing certificate (Certum, Sectigo, etc.) | ~$70-$300/year | Since 2023 keys must live on a hardware token/HSM, so a .pfx in CI is often not possible; cloud-HSM signing services cost extra. |
| EV certificate | ~$250-$500/year | Same hardware-key rules. |
