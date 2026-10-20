# Native app foundation and Android setup

Issue [#170](https://github.com/Sussman-Club/group-split/issues/170) is the tracker for
three ordered phases: a usable native app, signed installation packages, then push
notifications. The authentication and API foundation is implemented here; device
acceptance, invitation app links, release packaging and notifications remain open.

## Build configuration

Install the .NET SDK from `global.json`, the `maui-android` workload, Android SDK and
JDK required by that workload. Build only the Android target on Windows or Linux:

```powershell
dotnet workload install maui-android
dotnet build src/GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj `
  -p:AndroidOnly=true -f net10.0-android -c Debug `
  -p:NativeApiUrl=https://api.example.com/ `
  -p:NativeAuthority=https://identity.example.com/realms/group-split
```

Replace both example addresses. `NativeAuthority` is the full realm URL, not the
Keycloak server root. Debug builds must supply both values; Android Release uses the
production defaults below. They are public addresses embedded in assembly metadata;
never put credentials in them. Release accepts HTTPS only. Debug also accepts HTTP
and permits Android cleartext traffic. Certificate validation is never bypassed.

The native API URL must be reachable from the device and accept bearer tokens directly.
Production exposes `https://groupsplit.sussman.win/native/api/`; the API container itself
stays private. The cookie-based `/api` route is for the web app. The production authority
is `https://groupsplit.sussman.win/idp/realms/group-split`. These are the Android Release
defaults and can be overridden by build properties. Debug still requires explicit URLs.

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
`groupsplit-upload`, uses `file:` password references, and checks that a signed bundle
was produced in `artifacts/android`. Increment `BuildVersion` for each Play upload.
Pass `-JavaSdkDirectory` and `-AndroidSdkDirectory` when those SDKs are not discovered
automatically. `-Format apk` produces a signed APK for sideloading instead.

If Windows workload installation cannot start its administrator helper, a user-local
.NET SDK can host the workload. Pass its executable with
`-DotnetPath "$env:LOCALAPPDATA/GroupSplit/Toolchain/dotnet/dotnet.exe"` and install/update
the `maui-android` workload using that same executable. The app pins MAUI 10.0.110,
so the installed workload must support that package version.

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
  invitation app-link routing and public share-link origins still need implementation.
- Bank-link and reconnect controls remain hidden in the app while web linking still works.

The application identity and local signing command are configured. Phase 2 still needs
final artwork, signed release CI, repository signing secrets and device installation
validation. The current minimum Android
API level is 24; that is still a release decision. Phase 3 follows a verified installable
app and must choose push transport, add a transactional outbox and background delivery,
device registration, preferences, Android 13 permission handling and destination routing.
No push tokens or notification permissions are collected by this foundation.
