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
Keycloak server root. Both values are required build properties, so each build
configuration must supply them. They are public addresses embedded in assembly metadata;
never put credentials in them. Release accepts HTTPS only. Debug also accepts HTTP
and permits Android cleartext traffic. Certificate validation is never bypassed.

The native API URL must be reachable from the device and accept bearer tokens directly;
the web BFF's cookie endpoints are not a native API. Exposing the production API for
native access must be verified before release.

For an Android emulator, `localhost` is the emulator itself. Use `10.0.2.2` plus the
host's published API/Keycloak port for local development. Set both addresses explicitly;
there is no native Aspire service discovery. The system browser and the app must reach
the same Keycloak authority. Keycloak's configured frontend hostname, browser redirects,
and API issuer validation must agree with that address. Physical devices need reachable
LAN or deployed HTTPS addresses; `10.0.2.2` is emulator-only. A host's trusted development
certificate is not automatically trusted on a device.

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

## Validation and outstanding acceptance

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

Phase 2 must establish the real application identity and artwork, signed release CI,
keystore secrets, versioning and sideloading instructions. The current minimum Android
API level is 24; that is still a release decision. Phase 3 follows a verified installable
app and must choose push transport, add a transactional outbox and background delivery,
device registration, preferences, Android 13 permission handling and destination routing.
No push tokens or notification permissions are collected by this foundation.
