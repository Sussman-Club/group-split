# Android notifications

The Android MAUI app can opt in to Firebase Cloud Messaging (FCM) alerts under **Account → Notifications**. Each category has an independent switch on each device. Android permission is requested only when enabling a category; one account can register several devices. All categories default to off.

Available categories:

| Category | When it sends | Recipients / tap destination |
| --- | --- | --- |
| Expenses | An expense is added, edited, moved, or deleted | Other current members of the affected groups; opens the group |
| Settlements | A repayment is recorded or a member requests repayment | Repayments alert current members on either end except the actor and open the group; requests alert the named recipient and open the request |
| Guests joining | A guest accepts a shareable invitation | Other current group members; opens the group |
| Bank sync issues | A bank newly requires sign-in, revokes access, warns of expiring sign-in, or reports unshared accounts | Only the bank owner; opens Account |

Bank alerts are for actionable state changes. Repeated webhook reports of the same issue do not enqueue another alert, and ordinary transient sync retries do not send alerts. There is no in-app notification history. Automatically filed bank expenses are not wired to the new-expense event yet.

## Create the Firebase project

1. Open the [Firebase console](https://console.firebase.google.com/) and create a project. Analytics is optional and is not needed for push delivery.
2. Add an Android app with package name **club.sussman.groupsplit**. This must match the MAUI ApplicationId.
3. Download `google-services.json` and place it at `src/GroupSplit.App/GroupSplit.App/Platforms/Android/google-services.json`. The project includes it with the GoogleServicesJson build action when present. It is ignored by Git. CI needs to supply this file before building an app with push enabled.
4. Confirm the **Firebase Cloud Messaging API (HTTP v1)** is enabled in the project's Google Cloud APIs settings.
5. Rebuild and install the Android app. An emulator must have Google Play services; a physical Google Play device also works. Without the Firebase configuration file the app builds normally and hides the notification controls.

See [Firebase Android setup](https://firebase.google.com/docs/cloud-messaging/android/get-started).

## GitHub production environment

The release workflows read these settings from **Settings / Environments / production**:

| Setting | Kind | Purpose |
| --- | --- | --- |
| `ANDROID_GOOGLE_SERVICES_JSON_BASE64` | Secret | Base64-encoded Android `google-services.json`; the Android release helper validates the package, writes it only for the build, and removes it afterwards. |
| `FIREBASE_SERVICE_ACCOUNT_BASE64` | Secret | Base64-encoded Firebase Admin service-account JSON; supplied only to the API container as a secret Aspire parameter. |
| `PUSH_PROJECT_ID` | Variable | The Firebase project ID, such as `sussman-club-groupsplit`. |
| `PUSH_ENABLED` | Variable | `true` to send notifications, `false` to disable sending. |

The Deploy workflow carries the API settings through Compose to Komodo. When enabled, missing push configuration fails validation before image builds. The Android release workflow refuses missing or wrong-package Firebase configuration rather than silently publishing an app without notifications. Both workflows use the same production Firebase project; the server credential never enters an Android build. Base64 is only the transport encoding; GitHub encrypts the secrets.

## Configure the API

Apply the `AndroidPushNotifications` EF migration using the normal database deployment workflow before deploying this API. It adds `PushDevice` registrations; deliveries use the shared `BackgroundJob` queue already in `dev`.

Configure the **API process** (not the web frontend) with:

```text
Push__Enabled=true
Push__ProjectId=<your Firebase project ID>
GOOGLE_APPLICATION_CREDENTIALS=<absolute path to the server service-account JSON>
```

Production can instead set `Push__ServiceAccountBase64` to the base64-encoded service-account JSON. The API loads it in memory using the service-account-only Google credential factory; no key file or host-specific path is required. Leave it empty to use Application Default Credentials as above.

Use an identity with Firebase Cloud Messaging send permissions in this project. Application Default Credentials through workload identity can replace the credentials file. Keep service-account JSON in server secret storage; never place it in the Android app or commit it. The `google-services.json` Android configuration is a different file and is not a server credential.

Push defaults to disabled. Disabled sending fails safely without contacting Firebase; queued jobs follow the shared retry policy and are skipped once expired. For local Aspire runs, set these values in the AppHost environment; it forwards them to the API when push is enabled. Deployed containers need these values supplied explicitly. No Firebase project or credentials are provisioned by this branch.

See [Firebase Admin setup](https://firebase.google.com/docs/admin/setup) and [sending messages](https://firebase.google.com/docs/cloud-messaging/send/admin-sdk).

## Delivery behavior

- Device registration is authenticated and bound to the current account. Only that account can change or unregister it. Token rotation updates the same installation; token uniqueness prevents duplicate registrations.
- Each event stages its pending deliveries in the same database commit as the business change. Firebase is called by typed handlers through the existing durable background queue, outside the expense request. Job payloads contain device/user/group identifiers, a stable delivery ID, and expiration; FCM tokens are read only when sending.
- The worker checks the selected category, device ownership, and current membership again before sending. Payment requests must still be open and addressed to the recipient; bank issues must still exist on the recipient's bank connection. The shared queue polls every two seconds and provides atomic multi-instance claims, five-minute leases, a two-minute execution timeout, and up to ten attempts with exponential backoff (five seconds through one hour). Failed jobs remain available for operator inspection; completed payloads are cleared. Delivery is at least once: a process failure after Firebase accepts a message can cause a retry. Notification tags reduce duplicate tray entries.
- Unregistered FCM tokens are deleted. Registrations last 30 days without a successful app refresh. Pending alerts expire after one hour. The app refreshes registration on resume, sign-in and token rotation.
- Each preference category has its own Android notification channel. Android displays background notifications; the messaging service displays foreground notifications. Background messages set an explicit current UTC event timestamp, preventing the Firebase SDK's default year-one timestamp from showing an age of thousands of years. Tapping opens the group through the native router, including a cold start. Group access still requires the normal authorization checks.
- Sign-out unregisters the installation, turns off auto-init, deletes its FCM token and clears tray notifications. Account deletion removes its device records. A different account signing in resets device alerts. Offline failures show a warning; alerts already handed to FCM may still be in flight.
- Group alerts include the group name on the lock screen (for example, "An expense was added to Vacation 2024."), with no member names, amounts or expense descriptions. Bank alerts contain only a generic notice that a linked bank needs attention. Tokens and provider errors are not logged.

## Check live delivery

1. Apply the migration and run the configured API.
2. Sign into the Android app as a member of a shared group. Enable the categories being tested in Account, and accept Android's permission prompt (Android 13+).
3. As a different member, add an expense to the group. The actor should not receive their own alert.
4. Check the receiving app while foregrounded, backgrounded and closed. Tap each alert and confirm the group opens. Android force-stop blocks FCM until the app is opened again.
5. Disable one category and confirm other selected categories remain enabled. Test expense edits/deletions, repayments, payment requests, guest acceptance, and bank issues. Verify each tap destination. Then re-enable and sign out, and repeat. Check a second device, denied OS permissions, a removed group member and account switching.

Firebase credentials and an Android device are required for these live checks; local builds and automated tests do not verify actual FCM delivery.

## Extend to iOS and other notification events

`PushNotificationKinds.Definitions` is the shared event catalog: each event has its own kind and message, and belongs to one of the four preference categories. UI settings and Android channels come from those categories. Add an event to an existing category and stage `SendPushNotification` through `PushNotificationQueue` in the same save as its business change. Add any event-specific authorization or relevance checks to the delivery handler, and any new destination to the shared route helper. Persisted enum values must stay stable. New preference categories still need an explicit device preference and schema change.

The Firebase sender and device registration API accept FCM tokens without assuming Android. The message includes common notification text and routing data, plus separate Android and APNs delivery settings. Apple alerts have a one-hour expiry, default sound, and a group thread. Both platforms use the same queue, preferences, recipient checks, and server credential.

This prepares server delivery for iOS; it does not implement the iOS client or claim an iPhone test. To ship iOS, register the iOS bundle ID in the same Firebase project, supply `GoogleService-Info.plist` to an iOS build on macOS, upload the Apple APNs authentication key to Firebase, enable the app's push capability, and implement `IPushNotifications` with the iOS Firebase SDK. Register the Firebase **FCM** token (not the raw APNs token), and route foreground/tap callbacks through the shared notification kind and native link router. The four settings remain the same. Keep Apple credentials separate from the shared Firebase Admin credential.
