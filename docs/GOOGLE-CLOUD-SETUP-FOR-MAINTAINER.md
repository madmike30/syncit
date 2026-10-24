# Google Cloud setup for the maintainer (built-in "Sign in with Google")

Official builds can include a built-in OAuth **Desktop** client, so users only click **Sign in with Google**. You create this client once in your own Google Cloud project and store it as two GitHub secrets. The values are injected at build time and are never committed.

## 1. Create the project
1. <https://console.cloud.google.com/projectcreate>. Name: `SyncIt`. Use your personal account, not a work organization.
2. Enable the Drive API: <https://console.cloud.google.com/apis/library/drive.googleapis.com>, then **Enable**.

## 2. Consent screen (Google Auth Platform)
1. **Branding** (<https://console.cloud.google.com/auth/branding>): App name `SyncIt`. User support email: your address. Optional: logo, plus home page `https://github.com/madmike30/syncit`. Privacy policy and terms URLs are needed later for verification (they can point to repo docs).
2. **Audience** (<https://console.cloud.google.com/auth/audience>): User type **External**.
3. **Data access** (<https://console.cloud.google.com/auth/scopes>): **Add or remove scopes**, then add `https://www.googleapis.com/auth/drive`. It's a *restricted* scope.
4. Publishing status: click **Publish app** (Production, unverified). Recommended:
   - In **Testing**, only listed test users (max 100) can sign in, and refresh tokens expire after **7 days**, so users would have to sign in again weekly.
   - In **Production, unverified**, anyone can sign in after clicking through the **"Google hasn't verified this app"** screen, and tokens don't expire weekly.

## 3. Create the Desktop client
<https://console.cloud.google.com/auth/clients/create>: Application type **Desktop app**, name `SyncIt desktop`, then **Create**. Copy the Client ID and Client secret.

## 4. Add the GitHub secrets
<https://github.com/madmike30/syncit/settings/secrets/actions>, then **New repository secret**:
- `SYNCIT_GOOGLE_CLIENT_ID`: the Client ID
- `SYNCIT_GOOGLE_CLIENT_SECRET`: the Client secret

Or from a terminal (the value is prompted for and not echoed):
```
gh secret set SYNCIT_GOOGLE_CLIENT_ID -R madmike30/syncit
gh secret set SYNCIT_GOOGLE_CLIENT_SECRET -R madmike30/syncit
```
The next `v*` tag builds a release with the built-in client. For a local build with it: set the same two environment variables, then run `publish.bat`. Without them, local builds use bring-your-own-client mode.

> Google's installed-app guidance treats a desktop client secret as **not confidential**: anyone can extract it from the exe. That's expected. Security comes from the user's consent in the browser and the browser-based localhost (loopback) redirect, not from hiding the secret. Don't reuse this project for anything else.

## 5. Limits while unverified
- **100-user cap.** An unverified app that requests sensitive or restricted scopes can be authorized by at most **100 new users over the app's lifetime**. The count never resets, even if users revoke access. After that, new users get an error. SyncIt detects this and offers the optional **Advanced: use my own Google client** setup.
- Every user sees the **unverified app** warning. The SyncIt setup screen explains how to click through it.
- Google can suspend unverified apps that get abuse reports.
- Usage of the cap is shown on the **Audience** page.

## 6. Getting verified (removes the warning and the cap)
Because `drive` is a **restricted** scope:
1. Verify domain ownership of the home page and privacy policy, for example a custom domain or GitHub Pages on a domain you own, through Google Search Console.
2. Publish a privacy policy that describes Drive data use, and meet the Google API Services User Data Policy, including Limited Use.
3. Submit for verification on the **Verification Center** page: scope justification plus a demo video showing the OAuth flow and how Drive data is used.
4. Pass a **CASA security assessment** (Cloud Application Security Assessment) through an authorized lab. It's required for restricted scopes, repeated yearly, and usually costs money (from a few hundred dollars up, depending on tier and lab).
5. Expect several weeks of back-and-forth.

Until then, Production-unverified plus the advanced fallback is the practical setup.
