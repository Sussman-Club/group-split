# Android expense notifications

The Android MAUI app can opt in to Firebase Cloud Messaging (FCM) expense alerts under **Account → Notifications → Expense alerts**. Permission is requested only after that action. Each device has its own switch; one account can register several devices.

The first implementation alerts other current group members when an expense is created through the transaction service. It does not yet send settlement or invitation alerts, or keep an in-app notification history. Automatically filed bank expenses are not wired to this event yet.

## Create the Firebase project

1. Open the [Firebase console](https://console.firebase.google.com/) and create a project. Analytics is optional and is not needed for push delivery.
2. Add an Android app with package name **club.sussman.groupsplit**. This must match the MAUI ApplicationId.
3. Download `google-services.json` and place it at `src/GroupSplit.App/GroupSplit.App/Platforms/Android/google-services.json`. The project includes it with the GoogleServicesJson build action when present. It is ignored by Git. CI needs to supply this file before building an app with push enabled.
4. Confirm the **Firebase Cloud Messaging API (HTTP v1)** is enabled in the project's Google Cloud APIs settings.
5. Rebuild and install the Android app. An emulator must have Google Play services; a physical Google Play device also works. Without the Firebase configuration file the app builds normally and hides the notification controls.

See [Firebase Android setup](https://firebase.google.com/docs/cloud-messaging/android/get-started).

## Configure the API

Apply the `AndroidPushNotifications` EF migration using the normal database deployment workflow before deploying this API. It adds `PushDevice` registrations; deliveries use the shared `BackgroundJob` queue already in `dev`.

Configure the **API process** (not the web frontend) with:

```text
Push__Enabled=true
Push__ProjectId=<your Firebase project ID>
GOOGLE_APPLICATION_CREDENTIALS=<absolute path to the server service-account JSON>
```

Use an identity with Firebase Cloud Messaging send permissions in this project. Application Default Credentials through workload identity can replace the credentials file. Keep service-account JSON in server secret storage; never place it in the Android app or commit it. The `google-services.json` Android configuration is a different file and is not a server credential.

Push defaults to disabled. Disabled sending fails safely without contacting Firebase; queued jobs follow the shared retry policy and are skipped once expired. These environment values must reach the API container/process explicitly if an orchestrator launches it; they are not automatically forwarded by the AppHost. No Firebase project or credentials are provisioned by this branch.

See [Firebase Admin setup](https://firebase.google.com/docs/admin/setup) and [sending messages](https://firebase.google.com/docs/cloud-messaging/send/admin-sdk).

## Delivery behavior

- Device registration is authenticated and bound to the current account. Only that account can change or unregister it. Token rotation updates the same installation; token uniqueness prevents duplicate registrations.
- Creating an expense saves its pending deliveries in the same database commit. Firebase is called by a typed `SendExpensePush` handler through the existing durable background queue, outside the expense request. Job payloads contain device/user/group identifiers, a stable delivery ID, and expiration; FCM tokens are read only when sending.
- The worker checks membership and device preference again before sending. The shared queue polls every two seconds and provides atomic multi-instance claims, five-minute leases, a two-minute execution timeout, and up to ten attempts with exponential backoff (five seconds through one hour). Failed jobs remain available for operator inspection; completed payloads are cleared. Delivery is at least once: a process failure after Firebase accepts a message can cause a retry. Notification tags reduce duplicate tray entries.
- Unregistered FCM tokens are deleted. Registrations last 30 days without a successful app refresh. Pending alerts expire after one hour. The app refreshes registration on resume, sign-in and token rotation.
- Android displays background notifications; the messaging service displays foreground notifications. Tapping opens the group through the native router, including a cold start. Group access still requires the normal authorization checks.
- Sign-out unregisters the installation, turns off auto-init, deletes its FCM token and clears tray notifications. Account deletion removes its device records. A different account signing in resets device alerts. Offline failures show a warning; alerts already handed to FCM may still be in flight.
- Lock-screen messages contain a generic expense alert, with no names, amounts or expense descriptions. Tokens and provider errors are not logged.

## Check live delivery

1. Apply the migration and run the configured API.
2. Sign into the Android app as a member of a shared group. Enable expense alerts in Account, and accept Android's permission prompt (Android 13+).
3. As a different member, add an expense to the group. The actor should not receive their own alert.
4. Check the receiving app while foregrounded, backgrounded and closed. Tap each alert and confirm the group opens. Android force-stop blocks FCM until the app is opened again.
5. Disable alerts and repeat; then re-enable and sign out, and repeat. Check a second device, denied OS permissions, a removed group member and account switching.

Firebase credentials and an Android device are required for these live checks; local builds and automated tests do not verify actual FCM delivery.
