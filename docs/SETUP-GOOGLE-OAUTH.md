# Set up your Google OAuth client

SyncIt talks to Google Drive with an OAuth client that **you** own. It takes about 5 minutes, once.

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create a project (any name, e.g. `syncit`).
2. **APIs & Services > Library**: enable **Google Drive API**.
3. **APIs & Services > OAuth consent screen** (Google Auth Platform > Branding/Audience):
   - User type: **External**. App name: `SyncIt`. Add your email as support and developer contact.
   - Under **Audience > Test users**, add the Google account(s) you will sync.
   - You do not need to publish the app or go through verification for personal use (test mode allows up to 100 test users). In test mode Google may expire the sign-in after 7 days; SyncIt will ask you to sign in again.
4. **APIs & Services > Credentials > Create credentials > OAuth client ID**:
   - Application type: **Desktop app**. Name: `SyncIt desktop`.
5. Copy the **Client ID** (ends with `.apps.googleusercontent.com`) and the **Client Secret** (usually starts with `GOCSPX-`).
6. Start SyncIt. On the setup screen paste both values. SyncIt checks the format before continuing.
7. Click **Sign in**. Your browser opens; choose the account and allow access. Successful sign-in is the end-to-end test of the client.

## Troubleshooting
| Error | Fix |
|---|---|
| `access_denied` / "app has not completed verification" | Add the account under **Test users** (step 3). |
| `invalid_client` | Client ID/Secret mismatch or wrong type. Recreate as **Desktop app**. |
| `redirect_uri_mismatch` | You created a *Web* client. Use **Desktop app**. |
| "Google Drive API has not been used" | Enable the Drive API (step 2). |

## Where the values are stored
Client ID/Secret: Windows Credential Manager. Refresh token: `%APPDATA%\SyncIt\token.bin`, DPAPI-encrypted. See [SECURITY.md](../SECURITY.md).

## Why SyncIt needs the full `drive` scope
SyncIt requests one scope: `https://www.googleapis.com/auth/drive`. The narrower `drive.file` scope only shows files that SyncIt itself created or that you open through the Google Picker web widget. SyncIt's main job is to sync **existing** Drive folders (and everything created in them later from the web, phones or other computers). Under `drive.file` those files would be invisible, the folder browser could not list your Drive, and the Changes feed would miss edits made elsewhere. So `drive.file` would break core sync, and the full scope stays. No other scopes are requested.

## Next step (maintainer): a shared, verified client
To let users skip this page, the maintainer would ship a built-in client from a Google Cloud project that passed Google's OAuth verification. Because `drive` is a *restricted* scope, that requires a privacy policy, a domain, a demo video and a yearly third-party security assessment (CASA), which has a cost. Until then, bring-your-own-client is the supported path.
