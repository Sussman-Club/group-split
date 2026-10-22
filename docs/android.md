# Native app foundation and Android setup

Issue [#170](https://github.com/Sussman-Club/group-split/issues/170) is the tracker for
three ordered phases: a usable native app, signed installation packages, then push
notifications. This PR covers the Android app, invitation links, signed packaging and
website downloads. Notifications are explicitly outside this PR.

## Build configuration

Install the .NET SDK from `global.json`, the `maui-android` workload, Android SDK and
JDK required by that workload. Android is the default native target on every host; opt
into the other platform heads explicitly with `-p:AndroidOnly=false` and their required
workloads. Build Android on Windows or Linux:

```powershell
dotnet workload install maui-android
dotnet build src/GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj `
  -p:AndroidOnly=true -f net10.0-android -c Debug `
  -p:NativeApiUrl=https://api.example.com/ `
  -p:NativeAuthority=https://identity.example.com/realms/group-split
```

Replace both example addresses. `NativeAuthority` is the full realm URL, not the
Keycloak server root. The native configurations use the production defaults below.
They are public addresses embedded in assembly metadata;
never put credentials in them. Both configurations default to the production HTTPS
addresses below; supply explicit addresses when testing against local services. Release accepts HTTPS only. Debug also accepts HTTP
and permits Android cleartext traffic. Certificate validation is never bypassed.

The native API URL must be reachable from the device and accept bearer tokens directly.
Production exposes `https://groupsplit.sussman.win/native/api/`; the API container itself
stays private. The cookie-based `/api` route is for the web app. The production authority
is `https://groupsplit.sussman.win/idp/realms/group-split`. These are the Android Release
defaults and can be overridden by build properties.

`NativeWebUrl` is the public website used for shared invitation URLs. It defaults to
`https://groupsplit.sussman.win/`; override it for local testing. The WebView's private
origin is never used in links copied by the native app. The Android HTTPS intent filters
target the production website; a different deployment host also needs matching filters
and its own website association.

For an Android emulator, `localhost` is the emulator itself. Use `10.0.2.2` plus the
host's published API/Keycloak port for local development. Set both addresses explicitly;
there is no native Aspire service discovery. The system browser and the app must reach
the same Keycloak authority. Keycloak's configured frontend hostname, browser redirects,
and API issuer validation must agree with that address. Physical devices need reachable
LAN or deployed HTTPS addresses; `10.0.2.2` is emulator-only. A host's trusted development
certificate is not automatically trusted on a device.

## Build and sign a release

The application ID is `club.sussman.groupsplit` and the display name is Group Split.
Release Android builds default to an Android App Bundle (`aab`). The signing key is an
upload key when used with Google Play App Signing; Google manages the distribution
signing key separately. Back up the upload keystore and its password securely.

Create an upload key once with the JDK's `keytool`, allowing it to prompt for its password:

```powershell
keytool -genkeypair -keystore "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.keystore" `
  -storetype PKCS12 -alias groupsplit-upload -keyalg RSA -keysize 2048 -validity 10000 `
  -dname "CN=GroupSplit Upload, O=Sussman Club"
```

Create the destination folder first and restrict access to your user. If an upload key
already exists, reuse it. Store the matching password in a protected file outside the
checkout, such as `groupsplit-upload.signing-password`, or use the file created during
initial setup. The helper never reads the password into its output or passes it as a
literal command argument.

Publish the MAUI project with the signing helper, replacing the example backend addresses:

```powershell
./tools/Publish-Android.ps1 `
  -ApiUrl https://groupsplit.sussman.win/native/api/ `
  -Authority https://groupsplit.sussman.win/idp/realms/group-split `
  -KeyStore "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.keystore" `
  -PasswordFile "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.signing-password" `
  -BuildVersion 1 -DisplayVersion 1.0.0
```

The helper calls `dotnet publish` in Release for the Android target, supplies the alias
`groupsplit-upload`, uses `file:` password references, and checks that a signed package
was produced in `artifacts/android`. Increment `BuildVersion` for each distributed release.
It uses distinct protected password files for the keystore and key because Android's
APK signer reads successive lines when the same file is reused. The temporary copy is
deleted on success or failure; the original password file is preserved.
Pass `-JavaSdkDirectory` and `-AndroidSdkDirectory` when those SDKs are not discovered
automatically. `-Format apk` produces a signed APK for sideloading instead.

If Windows workload installation cannot start its administrator helper, a user-local
.NET SDK can host the workload. Pass its executable with
`-DotnetPath "$env:LOCALAPPDATA/GroupSplit/Toolchain/dotnet/dotnet.exe"` and install/update
the `maui-android` workload using that same executable. The app pins MAUI 10.0.110,
so the installed workload must support that package version.

For Rider, check the exact **.NET CLI executable path** in **Settings → Build, Execution,
Deployment → Toolset and Build**. Run that executable with `workload list`: an Android
workload installed under a different SDK root does not satisfy Rider's selected SDK.
Install `maui-android` into the SDK Rider uses, and configure JDK 21 and the Android SDK
in its Android settings. Reload the solution after a workload installation if the old
errors remain. Machine-specific toolchain settings stay out of Git. The source Android
manifest includes the application package so Rider's Android manifest model can load it
during design-time analysis.

Microsoft's [command-line publishing guide](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0)
requires `file:` rather than `env:` password references for AAB signing. Do not place
keystores or password files in Git; ignore rules are a fallback, and the helper rejects
signing files inside the checkout. Signed packages built with example backend URLs are
packaging checks only and must not be submitted as a usable release.

Export the public upload certificate for Play Console when required:

```powershell
keytool -exportcert -rfc -alias groupsplit-upload `
  -keystore "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.keystore" `
  -file "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.pem"
```

The PEM contains only the public certificate. Upload the signed AAB through Play Console's
internal testing track first; this helper does not upload or publish to Google Play.

For a local package check, run the JDK's `jarsigner -verify` on the signed AAB and Google's
`bundletool validate --bundle=<signed-aab>`. Inspect the bundle manifest to confirm the
application ID, build version and target SDK. These checks validate the package, not
on-device behavior. Production must also have the reconciled `native` Keycloak client
before the installed app can sign in; publishing an AAB does not deploy realm changes.

## Signed release CI

The **Android release** workflow (`.github/workflows/android-release.yml`) is manually
triggered on `main` and uses the existing `production` Actions environment. Pull requests only run the separate Debug compile
check. A signed build uses the production HTTPS endpoints above, runs the shared app
tests, builds an APK and verifies its signature before uploading an artifact containing
`GroupSplit.apk` and `SHA256SUMS`. CI artifacts expire after 14 days; the workflow also
publishes these files in a versioned GitHub release for lasting website downloads.
This workflow does not deploy the server.

In the repository's **Settings → Environments → production**, add:

| Setting | Type | Value |
| --- | --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Secret | Base64 contents of the existing upload keystore |
| `ANDROID_SIGNING_PASSWORD` | Secret | Password shared by the keystore and upload key |
| `ANDROID_KEY_ALIAS` | Variable, optional | Existing key alias; defaults to `groupsplit-upload` |

Reuse the upload key used for previous releases. To set the secrets from PowerShell
with an authenticated GitHub CLI, pipe their values directly; do not print them:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.keystore")) |
  gh secret set ANDROID_KEYSTORE_BASE64 --repo Sussman-Club/group-split --env production
Get-Content -Raw "$env:LOCALAPPDATA/GroupSplit/Signing/groupsplit-upload.signing-password" |
  gh secret set ANDROID_SIGNING_PASSWORD --repo Sussman-Club/group-split --env production
```

After the workflow reaches the default branch, open **Actions → Android release → Run
workflow**, select `main`, enter a new integer build version and display version
(for example `1.0.0`). The workflow produces the website APK. Use a build version higher than
every previously distributed APK or uploaded AAB; rerunning a build does not increment
it automatically. Download the artifact from the successful run. Install the APK on a
device. The installed Release package still needs device acceptance against the deployed backend.

The workflow respects the production environment's existing branch policy; it does not
grant `dev` or pull-request branches access to production secrets.

`Publish-AndroidCI.ps1` creates signing files in a private temporary directory outside the
checkout. It clears secret environment variables before invoking the build and removes
the files in a `finally` block on success or failure. Signing secrets are scoped to that
step; they are not used by tests, dependency setup or pull-request builds. The workflow
uses GitHub-hosted ephemeral runners and does not cache signing material. Its helper
regression checks can be run without Android tools or real credentials:

```powershell
./tests/android-release.check.ps1
```

The native launcher icon and splash screen reuse the existing three-arc GroupSplit mark
and cream background. The Android foreground is inset to keep the mark inside adaptive
launcher masks; artwork is maintained with the shared brand assets in `docs/design-system.md`.

## Website downloads and updates

The public `/android` page is available from the website's home page and app bar, with
installation instructions for Android 7.0 (API 24) and newer. API 24 remains the supported
minimum for this release; older Android versions are unsupported. Signed-in and signed-out
visitors can download the app without registering another account or using an app store.

On 20 October 2026, the existing upload keystore and password were configured in the
repository's `production` environment as the two Android secrets above. The alias variable
is `groupsplit-upload`. The workflow runs only on `main`, respecting the environment's
existing branch restrictions. It publishes a draft `android-v<display-version>-<build-version>`
release with both files attached, then makes it public and marks it latest. Every published
release must have a new build version. Failed or cancelled draft publication can leave a
draft for a maintainer to inspect; published packages are not overwritten.
Android tags use their own prefix because `app-v*` is reserved for server deployment
version calculation.

The website checks the repository's latest public release, requiring an `android-v` tag,
`GroupSplit.apk`, a checksum file, and download URLs in this repository. It caches the
lookup for five minutes to limit GitHub API traffic. Missing, incomplete or removed
releases show an unavailable message with a retry button instead of a broken download.
A temporary GitHub outage preserves a previously verified download. Android releases
currently own the repository's latest release; the CLI publishes packages and tags rather
than GitHub releases.

People download the APK on their phone, open it, allow their browser to install apps if
prompted, and install Group Split. They can turn that browser permission off afterward.
Updates are new APK downloads from the same page, installed over the existing app with
the same signing key. Updates are manual; no background updater or notifications are
included. Do not uninstall an existing release merely to update it. A previous debug app
has a different signature and cannot be upgraded with the release APK.

## Invitation app links

HTTPS `/join/<token>` and `/claim/<token>` links on `groupsplit.sussman.win` are registered
with Android. The website serves `/.well-known/assetlinks.json` publicly without a
redirect, naming `club.sussman.groupsplit` and the public SHA-256 fingerprint of the
upload certificate used for direct APK signing. The signing key and password are never
included in this association. If the signing certificate changes, update the association
before distributing the replacement package.

The app queues an invitation arriving before the WebView is ready and routes it to the
shared join or claim page. An already-running app receives new links in the same window.
Signed-out people sign in through the normal browser flow and return to that invitation;
cancelled or failed sign-in leaves a retryable message and retains the destination.
The app accepts only these two route shapes, from the configured website or
`groupsplit://invite/join/<token>` / `groupsplit://invite/claim/<token>`. Foreign origins,
extra path segments, query strings and malformed tokens are rejected. Authentication
callbacks remain separate at `groupsplit://auth/callback`.

Deploy the association with the website before checking automatic HTTPS app-link opening.
For an installed signed APK, Android's verification status and a direct invitation can
be checked with:

```powershell
adb shell pm verify-app-links --re-verify club.sussman.groupsplit
adb shell pm get-app-links club.sussman.groupsplit
adb shell am start -a android.intent.action.VIEW -c android.intent.category.BROWSABLE `
  -d 'https://groupsplit.sussman.win/join/<real-token>'
```

See Android's [app-link verification guidance](https://developer.android.com/training/app-links/verify-applinks).
The custom-scheme path can be tested before deployment, but it does not prove website
association or authorization to join a particular group; the API still checks every token.

## Sign-in and persistence

The realm worker creates the `native` public client on existing realms and reconciles
its exact callback, S256 PKCE, allowed grants, refresh tokens and API audience. Deploy/run
the realm worker before testing sign-in. No native client secret is required.

The app opens the system browser and handles `groupsplit://auth/callback` with an Android
callback activity. It verifies the random state and exchanges the authorization code
with a fresh PKCE verifier. Identity and authorization come from the bearer-protected
API; the returned ID token is used only as the authority's logout hint.

"Keep me signed in" saves the token set in MAUI SecureStorage; leaving it unticked keeps
the session in memory until the process exits. Android backups are disabled to prevent
restoring encrypted credentials without their encryption key. API handlers share one
session, refresh before access-token expiry, and persist rotated refresh tokens before
using them. A revoked refresh token clears the session; a temporary network failure
preserves it for retry. A 401 clears the session and lets the shared error presenter
return to sign-in. Failed writes are never automatically replayed.

Sign-out clears local credentials before asking the authority to end its browser session.
If the browser is cancelled or unavailable, local sign-out still completes; authority
sign-out may need to be retried in the browser. Account deletion only clears local state.
A separate non-secret preferences marker disables restoration before credentials are
removed. If secure-storage deletion fails, the old credentials remain disabled across
process restarts; only successfully saving a new remembered session enables restoration
again. If neither deletion nor the marker can be saved, sign-out reports that saved
sign-in could not be cleared instead of claiming it completed.

Bank linking and reconnection are unavailable in the embedded native WebView until
Hosted Link is implemented. The account and empty inbox direct people to the web app.

## Native implementation patterns

`MauiProgram` composes the app through `AddNativeServices`; the container owns the token
session and platform adapters. Pages are transient and resolved after application resources
initialize. User and navigation services live in the BlazorWebView scope, while the token
session is app-wide. This follows Microsoft's [MAUI dependency injection guidance](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/dependency-injection?view=net-maui-10.0).

The token session requests a fresh named client from `IHttpClientFactory` for each identity
exchange, rather than capturing a client for the lifetime of the app. Identity requests have
a 30-second timeout. Identity and API handlers disable cookie sharing and automatic
redirects; only API handlers attach bearer tokens. See [HTTP client lifetime guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory).

The browser adapter takes `IWebAuthenticator` through its constructor and opens it on the
UI thread. The storage adapter takes `ISecureStorage`, discards unreadable session data
without clearing unrelated keys, and reports persistence failures without storing tokens
in plaintext. These follow [main-thread guidance](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/appmodel/main-thread?view=net-maui-10.0)
and [secure-storage recovery guidance](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0).
Android callback support is implemented; desktop callbacks still require platform-specific
implementation and validation.

## Validation and outstanding acceptance

For Android tests against local services, start the AppHost's HTTP profile with
`NativeDeviceTesting=true` and `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` in its environment.
This run-mode-only opt-in serves Keycloak over HTTP on loopback port 8180; normal runs
retain Aspire's development TLS certificate and publishing is unaffected.
Build the Debug app with `NativeApiUrl=http://localhost:5144/` and
`NativeAuthority=http://localhost:8180/realms/group-split`. For each Android device,
use `adb -s <serial> reverse tcp:5144 tcp:5144` and
`adb -s <serial> reverse tcp:8180 tcp:8180` before launching it. Keep this configuration
for local Debug tests only. Do not disable certificate validation in the app.

Local checks on 19 October 2026 used an Android 36 emulator and a physical Pixel 8a.
Both installed, launched, signed in through the system browser and loaded authenticated
groups. The emulator created a dedicated group, saved expenses, split $20 with a guest,
showed the expected $10 balance and recorded a settlement back to zero. Keycloak logs
confirmed automatic refresh. Restarting the emulator app discarded its temporary session.
Enabling "Keep me signed in" restored the session after a process restart. Sign-out
exposed an account-menu render race; its deferred content now captures the signed-in
user, and a regression test covers rendering that content after the session is cleared.
These checks found a status-bar overlap: `MainPage.SafeAreaEdges="All"` now reserves system
bar, cutout and keyboard space, following the [MAUI safe-area guidance](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/safe-area?view=net-maui-10.0).
The corrected header and bottom navigation were visually checked on both devices.

Checks on 20 October 2026 added 1,017 passing shared app/native tests, including rejected
link destinations, public download metadata, canceled sign-in and interrupted writes.
Android Debug builds complete with zero errors. An updated Debug app installed and
launched on the Android 36 emulator; a join link opened in an already-running app and a
claim link opened from a cold launch. Recreating the Android activity exposed a delivery
race between old and visible WebViews; incoming routes are now broadcast to listeners,
and the corrected claim destination was visually verified after activity recreation.
These used placeholder invitation tokens to check navigation without changing membership.
Canceling the system-browser sign-in returned to the same claim page with a visible
"Sign-in was cancelled" message and an enabled retry button.
The website's unavailable-download state was checked at desktop and 390-pixel phone
widths with no horizontal overflow or browser-console errors, and its public association
endpoint returned HTTP 200 with the expected Android package. The local AppHost was
stopped after these checks. A local signed Release APK's signature was verified with
`apksigner`, matching the public fingerprint in the website association.

Offline reads/writes, revocation, account deletion and invitation links still require
acceptance checks; these results are not a completed release sign-off.

Platform-independent session and bearer-handler tests run with the existing web suite:

```powershell
dotnet test --project tests/GroupSplit.App.Web.Test -c Release -- --filter-namespace GroupSplit.App.Web.Test.Native
dotnet test --project tests/GroupSplit.Seeder.Test -c Release
```

The session tests use fake browser, storage and HTTP adapters; they do not validate the
Android callback, SecureStorage implementation, system browser, networking or UI on-device.
The Android build workflow compiles the Android head on relevant pull requests with
example endpoints. It does not run the app or sign a release.
Before declaring phase 1 complete, build the Android head and validate on both an emulator
and physical device:

- Fresh sign-in, cancelled sign-in, process restart with and without persistence, expired
  access token, revoked refresh token, authority sign-out and account deletion.
- Groups, ledger, adding an expense, balances and settlements. Lose the network during
  reads and writes, then recover without silently replaying a write.
- Invitation links, including arriving signed out and returning after sign-in. Android
  routing and public share-link origins are implemented; automatic HTTPS opening requires
  deploying the website association and checking verification on the installed release.
- Bank-link and reconnect controls remain hidden in the app while web linking still works.

The application identity, brand artwork, local signing command and manual signed release
CI and production signing secrets are configured. Phase 2 still needs a successful signed
workflow run from `main` and device installation validation of its released package. The minimum Android
API level is 24 (Android 7.0). Phase 3 follows a verified installable
app and must choose push transport, add a transactional outbox and background delivery,
device registration, preferences, Android 13 permission handling and destination routing.
No push tokens or notification permissions are collected by this foundation.

### In-app update notice

New Android builds embed their `ApplicationVersion` as `NativeAndroidBuild` assembly
metadata. On the first interactive layout render, the native app checks the public
`/downloads/android/latest` endpoint once. A release with a greater Android build
number shows a dismissible **Update available** notice. **Update app** opens the public
`/android` download page in the system browser. Installation remains a user action.
The notice stays dismissed during navigation and checks again on a fresh app session.
Offline, missing, invalid, and older releases do not interrupt the app. Website sessions
and builds without Android version metadata do not check for updates.

Existing APKs need to be updated once to a build containing this feature before they
can show future update notices.
