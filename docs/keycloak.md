# Keycloak

Keycloak owns every form that takes a credential, so the app never sees a password. This page
covers how sign-in is put together, how a sign-in becomes an account, what deleting an account
removes, how mail and Google sign-in are configured, what keeps a realm that already exists in
step with the repository, and what to check before changing any of it. Running it once
deployed is in [deployment.md](deployment.md).

## How sign-in works

Keycloak hosts the sign-in page, password reset, email verification, the Google link and the
account console. Keeping the forms there keeps SSO, the password policy and brute-force
protection in one place. The app's own sign-in page,
[`AuthHandoff`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/AuthHandoff.razor),
collects one answer, "Keep me signed in", and sends the browser to `/auth/login`, which starts
an OpenID Connect authorization code sign-in against the `group-split` realm. Why that checkbox
is the app's and not Keycloak's is under
[How long a session lasts](deployment.md#how-long-a-session-lasts).

Nobody signs themselves up. `registrationAllowed` is `false`, so Keycloak serves no
registration page and its login page has no "no account?" link, even for somebody who kept the
URL. An admin creates accounts in the admin console; locally the seeder creates the demo ones
(see [getting-started.md](getting-started.md)). Google signs in an existing account and never
creates one.

What the API does with the token it is handed is under
[Sign-ins and accounts](#sign-ins-and-accounts).

Deployed, Keycloak is served under `/idp` on the web app's own origin, and the web app forwards
only the app realm and its static files to it; see
[What the public origin exposes of Keycloak](deployment.md#what-the-public-origin-exposes-of-keycloak).
The admin console is reached through an SSH tunnel; see
[Reaching the deployed admin console](deployment.md#reaching-the-deployed-admin-console).

### The clients

| Client | Used by | What it may do |
| --- | --- | --- |
| `web-app` | The web app's server, which holds the tokens on the browser's behalf | Authorization code with PKCE (`S256`), public. Redirects only to `${WEB_HOSTNAME}/signin-oidc`, and back after sign-out only to `${WEB_HOSTNAME}/signout-callback-oidc`. Its tokens carry `api` in the audience. |
| `cli` | `groupsplit auth login` | The device authorization grant and nothing else, public, with `api` in the audience. A terminal cannot be given a redirect URI, and the browser that approves the sign-in is often on another machine. |
| `account-deletion` | The API, deleting the login of somebody who deleted their account | Client credentials only, confidential. Its service account holds `realm-management`'s `manage-users` and nothing else. See [The Keycloak client it uses](#the-keycloak-client-it-uses). |

A fourth client, `api`, has every flow switched off. It exists to be the audience: the API
accepts only tokens issued for `api`, which is why both clients people sign in through carry an
audience mapper for it.

The browser holds a cookie and no token. Where the tokens are kept instead is in
[Where the access token lives](deployment.md#where-the-access-token-lives); the key ring that
protects the cookie is [The sign-in key ring](deployment.md#the-sign-in-key-ring), and why a
deploy no longer signs people out is
[Sessions across a deploy](deployment.md#sessions-across-a-deploy).

### The realm file

[`realms.json`](../src/GroupSplit.AppHost/Assets/keycloak/realms.json) defines the
`group-split` realm: its settings, the four clients, the Google provider and its first broker
login flow, and which events are recorded. It holds no users apart from the `account-deletion`
service account, because it ships to deployments.

The same file reaches Keycloak by three routes:

- Locally, [`AsDevelopmentKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDevelopmentExtensions.cs)
  mounts it from the checkout for `--import-realm`, and Keycloak resolves its `${...}`
  placeholders from the container's environment.
- Deployed, [`AsDeployedKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDeploymentExtensions.cs)
  ships it as an inline Compose config, because a publish cannot express a bind mount to a
  file on the machine that ran it. Compose resolves the placeholders before Keycloak reads the
  file; see [Why the variable names have to match](#why-the-variable-names-have-to-match).
- [`GroupSplit.Keycloak`](../src/GroupSplit.Keycloak/KeycloakRealmDefinition.cs) embeds it,
  so the realm worker compares an existing realm against the file Keycloak imported.

Import creates a realm that does not exist and leaves one that does alone. Production's realm
and every developer's live on volumes and are never imported again, so a change to
`realms.json` reaches them only if the realm worker manages that setting.

### The realm worker

[`GroupSplit.RealmWorker`](../src/GroupSplit.RealmWorker/Program.cs) runs as the
`realm-settings` resource once Keycloak is ready: locally, and deployed as a one-shot Compose
service. It signs in to the admin API as a master-realm admin (locally Keycloak's bootstrap
admin, deployed the `keycloak-admin-user` parameter), brings the allowlisted settings back to
the file, and exits. The API waits for it to finish successfully, so a failed run keeps the
API from starting against a realm in an unknown state, and the worker's log, which also goes to
the Aspire dashboard, says why. What it manages is under
[Applying this to a realm that already exists](#applying-this-to-a-realm-that-already-exists).

### The theme

The realm names `group-split` as its login, account and email theme. The files are in
[`Assets/keycloak/themes`](../src/GroupSplit.AppHost/Assets/keycloak/themes), mounted from the
checkout locally and shipped as Compose configs when deployed, at
`/opt/keycloak/themes/group-split` either way:

- `login` parents `keycloak.v2` and adds a stylesheet, the logo and message overrides. It
  states `darkMode=true` because its stylesheet keys the dark palette on the class that
  switches on.
- `account` parents `keycloak.v3`, the account console, and adds a stylesheet and the logo.
- `email` parents `keycloak` and overrides message bundle entries only. The branding is in the
  `...BodyHtml` entries of `messages_en.properties`, since the base templates do nothing but
  emit them.

None of them copies a `.ftl` template, so a Keycloak upgrade cannot leave a page rendering
against a stale copy of Keycloak's markup. The colours, type and radii are copies of the app's
tokens; which file carries which, and how to check a change in both light and dark, is in
[design-system.md](design-system.md).

## Sign-ins and accounts

A `User` row is the person: their name, their shares, their place in other people's balances.
A `UserIdentity` row is the sign-in that answers to it, Keycloak's `sub`, opaque here.
[`UserProvisioner`](../src/GroupSplit.API/Services/UserProvisioner.cs) joins the two on every
authenticated request: it finds the row by `sub`, makes one on the first request from a `sub`
it has not seen, and copies the name and email from the token onto it. It never links a new
`sub` to an existing row by address.

`User.Email` is unique. That is where the `sub` and the address can disagree, and both ways
they can are logged as `IDENTITY_EMAIL_IN_USE`.

### A new sign-in with an address another row holds

What happens. Somebody is deleted in Keycloak and an admin creates them again with the same
address. Keycloak gives them a new `sub`. The app has never seen it, so it would make a new
row, and the old row still holds the address. Every request is refused with
`409 IDENTITY_EMAIL_IN_USE` ([errors.md](errors.md)). The web app shows "We could not open your
account" with a reference; the CLI exits `2` and says to contact support.

Why not link it automatically. Owning an address in Keycloak today is not proof of being the
person whose expenses are on the old row, and linking would hand that row (its balances, its
groups, its bank links) to whoever holds the new sign-in. That is a decision for a person to
make, once, with the facts in front of them.

What support does:

1. Find the log line by the reference (the trace id) the person quotes. It is a warning:
   `IDENTITY_EMAIL_IN_USE: sign-in <new sub> is new, and its email is already on user <holder id>. Refused until support resolves it.`
2. Read the holder's old `sub` from the database
   (`SELECT "IdentityId" FROM "UserIdentity" WHERE "UserId" = '<holder id>'`), look it up in
   the Keycloak admin console
   ([reaching it](deployment.md#reaching-the-deployed-admin-console)), and decide which case
   it is:

   - The same person, recreated. The old `sub` no longer exists in Keycloak, and the person
     confirms, out of band and not on the strength of the address, that the old account was
     theirs. Point the old row at the new sign-in, so they keep their history:

     ```sql
     UPDATE "UserIdentity" SET "IdentityId" = '<new sub>' WHERE "UserId" = '<holder id>';
     ```

     If the holder has no `UserIdentity` row at all, insert one with a new `Id`, that
     `UserId` and the new `sub`. Their next request goes through as the old row.

   - The same person, who wants a fresh start, or the old row belonged to somebody else who
     is gone. Free the address on the old row and leave the row itself alone. Other members'
     balances are made of it, which is also why deleting an account in the app clears the
     email and keeps the row:

     ```sql
     UPDATE "User" SET "Email" = NULL WHERE "Id" = '<holder id>';
     ```

     The next request makes a new, empty row for the new sign-in.

   - Two different people, both still in Keycloak. The old `sub` still exists and has the
     same address (a realm that allows duplicate emails, or an address moved between
     accounts). Nothing changes in the database: correct the address on the wrong Keycloak
     account, and the refusal stops on the next request.

Never delete the `User` row, and never point two identities at one row.

### An address change onto one another row holds

What happens. Somebody changes their email in Keycloak to an address another row holds. They
are already known by their `sub`, so they are not refused: the request goes on, the rest of the
profile still follows Keycloak, and the stored address stays as it was. A warning is logged on
every such request:
`IDENTITY_EMAIL_IN_USE: user <id> (sign-in <sub>) has an email in Keycloak that another user holds. Kept the stored email.`

Why not refuse it. The address is only displayed. Refusing would lock somebody out over it,
for good when two people swap addresses, since each would be waiting on the other.

What support does. Usually nothing: the next request tries again, so it resolves by itself once
the other row lets go of the address. A swap never does. Free one side and let the next
requests settle it:

```sql
UPDATE "User" SET "Email" = NULL WHERE "Id" = '<one of the two>';
```

The other person's next request takes the freed address, and then the first person's takes
theirs.

## Deleting an account

`DELETE /users/me`, which is the "Delete my account" button on the Account page and
`groupsplit users delete`, deletes the account in two places: the app's own database, and
Keycloak, where the login lives. This section records what that means, and how the two halves
are kept from disagreeing.

### The decision: the Keycloak user is deleted

Decided in [#360](https://github.com/Sussman-Club/group-split/issues/360). Deleting an account
deletes its Keycloak user. It is not disabled.

The alternative was to disable the user and remove its Google link, which would have kept a way
back: an operator could re-enable it. It was turned down because a person who deletes their
account is asking for their data to be erased, and the Keycloak user is most of that data:
their name, their email address, their password hash, their Google link, their sessions. A
disabled user still holds all of it, indefinitely, for a restore nobody asked for. Nothing in
the app needs the user afterwards either; the app's own row is what the ledger points at.

Before this, deleting an account left the Keycloak user untouched, and that showed:

- The person could still sign in. Their next request found no account for their subject, and
  the app quietly made them a new, empty one. From their side, deleting the account looked like
  it had not worked.
- Their identity data stayed in Keycloak: name, email, password hash, Google link, sessions.
- Their sessions stayed alive until they expired, up to 30 days for a remembered one.

### What the person loses, and what stays

| Gone | Stays |
| --- | --- |
| Their name and email address on the app's row. | The row itself, marked deleted and anonymised. Every other member's balance is calculated from the expenses and settlements it took part in, so deleting the row would rewrite other people's ledgers. Those records now show a deleted account. |
| Their membership of every shared group. | The expenses and settlements they recorded, in the groups they were recorded in. Their personal group stays with the anonymised row: it has no balance and nobody to leave it to. |
| Their Keycloak user: login, password, profile, Google link. | The Keycloak subject (an opaque id) on the anonymised row, as a tombstone; see [When Keycloak cannot be reached](#when-keycloak-cannot-be-reached). It names nobody once the user it identified is gone. |
| Every Keycloak session: the web app's, the CLI's refresh token, any other device. | |
| The web app's own session in the browser they deleted from. | |
| Linked banks, accounts, imported bank rows and unfiled receipt files. | Filed expenses lose their private bank-row link. Unremovable provider Items are recorded for administrator reconciliation. |
| Personal receipt files. | Shared receipt files and structured ledger/receipt history. File deletion jobs are saved in `BackgroundJob` with metadata removal and retried automatically. |

It cannot be undone. An admin creating a login with the same address, since registration is
closed, makes a new login and a new, empty account; it does not reach the old one.

Deleting is refused with `ACCOUNT_NOT_SETTLED` while any shared group's balance is not zero,
and then nothing changes in either place.

### How it is carried out

Before anonymisation, the app unlinks the account's banks and abandons pending links.
Provider failures leave the account active for retry; already unlinked banks stay removed.
Unreadable tokens or missing connectors leave credential-free provider cleanup records.
Personal receipt removals are queued atomically with anonymisation and performed after saving.
Backups expire according to the operator's configured policy; the application enforces no
fixed backup expiry. See [personal data and operational reconciliation](personal-data.md).

1. The app deletes its half, in one save. The row is anonymised and marked `Deleted`, its
   memberships end, and a `PendingLoginDeletion` row is written, all in the same transaction,
   so the login deletion is never owed without the account having gone, nor the other way
   round.
2. The same request then deletes the login, through Keycloak's admin API: it ends the user's
   sessions (`POST /users/{id}/logout`) and deletes the user (`DELETE /users/{id}`). On success
   the pending row is removed. This attempt waits at most ten seconds.
3. The web app signs the person out locally and shows the sign-in page with a note that the
   account and its sign-in were deleted. Locally only: the Keycloak sessions went with the
   user, and a Keycloak sign-out would only ask Keycloak to end a session it no longer has. The
   CLI throws away its stored credentials.

Deleting a Keycloak user removes its sessions and offline sessions, so step 2's sign-out is the
belt to that brace. It is the documented way to end sessions, and it runs first because after
the delete it would only answer 404. Either step finding no user counts as done, so a retry
after an unreported success is harmless.

### When Keycloak cannot be reached

The account is deleted regardless; it was saved before Keycloak was asked. What is left is a
login that should not exist, and two things keep that from becoming a silent mismatch:

- A durable retry. The `PendingLoginDeletion` row stays. `SweepPendingLoginDeletions` runs
  every five minutes and tries every row older than a minute again, until Keycloak confirms.
  Each failure is logged with the subject and the attempt count (a warning at first, an error
  from the third attempt on), and it never gives up, because giving up would leave the login
  standing for somebody who asked for it to go.
- The login is refused in the meantime. The anonymised row keeps its subject, so a sign-in
  with that login finds a deleted account rather than none. `UserProvisioner` refuses it with
  `403 ACCOUNT_DELETED`; it never provisions a fresh account in its place. The same refusal
  covers an access token issued before the deletion, which stays valid until it expires.

The person is not told their login still exists, because to them it does not: nothing will
sign in as it, and it is gone within minutes of Keycloak coming back.

### The Keycloak client it uses

The API never signs in as an administrator of the master realm. It uses `account-deletion`, a
confidential client in the `group-split` realm:

- client credentials only, with standard flow, implicit flow and direct access grants off;
- its service account holds `realm-management`'s `manage-users` and nothing else. That covers
  deleting a user and ending their sessions. `view-users` is not granted: the API never reads
  a user, and both calls it makes are `manage-users` operations;
- its secret is the `keycloak-account-deletion-secret` parameter.

It is defined in [`realms.json`](../src/GroupSplit.AppHost/Assets/keycloak/realms.json), so a
fresh realm imports it. A realm that already exists (production's, and every developer's,
which live on volumes and are never imported again) gets it from the `realm-settings` worker.
The worker creates the client when it is missing, as it is on every realm imported before the
client existed, and on every run holds it to the file: the flows above, the secret it was
given, and exactly the roles the file lists, revoking any added by hand, because the point of a
narrow client is that it stays narrow. So there is no manual step on an existing realm. The
API waits for that worker, so it never starts with a secret Keycloak does not know.

Locally the secret is generated and kept in the AppHost's user secrets. Deployed it is the
`KEYCLOAK_ACCOUNT_DELETION_SECRET` secret on the `production` environment; see
[Deployment parameters](deployment.md#deployment-parameters).

Rotating it is changing that secret and deploying. The worker compares the secret rather than
writing it blind, never logs it, and writes the new value to the client; then the API starts
with it. Between the worker and the API restarting, the old API cannot delete logins; anything
it tried is retried by the sweep.

### Accounts deleted before this

Accounts deleted before this change kept their Keycloak user and lost their subject here, so
nothing in the app records which login was theirs, and some of those people have signed in
since and own a fresh account. Those logins are not deleted automatically. Finding the ones
nobody uses is an operator's job at the admin console.

## Email

Keycloak sends the password reset and email verification mail, so it needs an SMTP relay.

Locally there is nothing to configure. The AppHost runs [Mailpit](https://mailpit.axllent.org/),
every message Keycloak sends lands in its inbox, and nothing leaves the machine. Open the inbox
from the `mailpit` resource in the Aspire dashboard. Mail is on locally, set with literal values
rather than parameters, because run mode mounts `realms.json` and Keycloak substitutes from the
container's own environment.

Deployed, the relay comes from the `production` GitHub environment. `SMTP_PASSWORD` is a
secret; the rest are variables:

| Name | Example | Required | Purpose |
| --- | --- | --- | --- |
| `SMTP_ENABLED` | `true` | no, defaults to `false` | The switch. `true` or `false`; anything else fails the publish rather than reading as off, so a typo cannot quietly switch mail off |
| `SMTP_HOST` | `smtp.resend.com` | when mail is on | Relay hostname |
| `SMTP_PORT` | `587` | no, defaults to `587` | Relay port. 587 is submission over STARTTLS, which is what the realm speaks. 465 wants implicit TLS, which is the realm's `ssl` flag, so it takes a change to `realms.json` as well |
| `SMTP_FROM` | `no-reply@example.com` | when mail is on | Sender address, on a domain the relay has verified. Defaults to `no-reply@group-split.invalid`; see below |
| `SMTP_USER` | `resend` | when mail is on | SMTP username |
| `SMTP_PASSWORD` | | when mail is on | SMTP password or API key |

With mail on, a missing host, sender, user or password fails the publish before anything is
built. The AppHost's `validate-smtp` pipeline step checks once the parameters are resolved, so
a publish from a laptop gets the same check, and the deploy workflow runs a cheaper copy of it
first. Nothing checks that the values describe a relay that works: wrong credentials pass, and
fail at the first send.

`SMTP_ENABLED` sets three values in `realms.json`: the relay's `auth` and `starttls`, and
`resetPasswordAllowed`, which decides whether the sign-in page offers "Forgot your password?".
A reset is a mail, so without a relay the form would accept an address and nothing would ever
arrive. With mail off the link is not shown, and somebody who has forgotten their password
signs in with Google, if it is on, or asks an operator to set a new one in the admin console.
Refusing to deploy password accounts without mail would block a deployment that otherwise
works for the sake of one link, so the link follows the mail instead. The realm worker is given
the same switch and applies `resetPasswordAllowed` to the existing realm on every deploy, so
turning mail on or off shows or hides the link without a realm reset. Locally the link is
there, and the mail lands in Mailpit.

The relay itself is not reconciled. The realm worker does not manage the `smtpServer` block, so
import writes it once, when the realm is created. On a realm that already exists, a new
`SMTP_HOST`, `SMTP_FROM` or credential changes the container's environment and not the realm,
and has to be set in the admin console as well (Realm settings, Email). That includes turning
mail on for a realm that was created with it off: the next deploy shows the reset link, and
the realm still has the empty relay it was imported with.

The realm does not require registrations to verify their address. A sender domain part way
through verification at the provider has every send rejected, and a verification requirement
would then strand users at their next login behind a mail that cannot arrive. Switch
`verifyEmail` on in `realms.json` once mail is flowing, if you want it at all.

Google sign-in follows the same shape. `GOOGLE_SIGN_IN_ENABLED`, a variable defaulting to
`false`, switches the provider on or off, and the secrets `GOOGLE_CLIENT_ID` and
`GOOGLE_CLIENT_SECRET` are required when it is on, checked by the `validate-google-sign-in`
step. Like the relay, the switch, client ID and secret reach the realm through import alone;
on a realm that already exists, change them under Identity providers in the admin console. The
parts of the provider the worker does hold are in
[the Google first sign-in](#which-account-a-first-google-sign-in-lands-in).

Whichever relay you use, expect to prove you own the sender domain by adding the SPF and DKIM
records it gives you. Mail from an unverified sender is rejected or filed as spam.

### Why the variable names have to match

Publishing does not mount `realms.json` as a file. The Compose publisher inlines its text into
a Compose config's `content`, and Compose interpolates `${...}` inside that text against its
own environment file before Keycloak ever reads it. So every placeholder in `realms.json` has
to spell a name that file defines, which is the screaming-snake form of the Aspire parameter it
comes from: `smtp-from` pairs with `${SMTP_FROM}`, `web-hostname` with `${WEB_HOSTNAME}`,
`keycloak-account-deletion-secret` with `${KEYCLOAK_ACCOUNT_DELETION_SECRET}`. Locally
Keycloak substitutes from the container's environment instead, so a placeholder that names a
container variable rather than a parameter works on a laptop and comes out blank once
deployed.

A name Compose cannot resolve is replaced with a blank string, and nothing complains except a
warning in the deploy log. Keycloak validates the sender address while importing the realm and
refuses to start on one it cannot parse, an empty string included, so the sender has to
resolve to an address even when mail is off. That is why `smtp-from` defaults to
`no-reply@group-split.invalid` rather than to nothing; `.invalid` is reserved and never routes.
Other blanks are quieter. An empty `WEB_HOSTNAME` would have the first import store
`/signin-oidc` as the only redirect URI, and every sign-in would fail.

## Applying this to a realm that already exists

The `realm-settings` worker starts after Keycloak is ready, in development and in a deployment.
It reads `realms.json`, compares the managed settings, and updates only the values that differ,
so a second run with no changes makes no API update. It resolves the placeholders in what it
manages, and only those, in one pass before it writes anything; a variable it was not given
fails the run and is named, with every other missing one. It is given only the switches a
managed setting follows, such as `SMTP_ENABLED`, and never the relay's password. If Keycloak
never becomes ready, the worker fails and the deployment does not silently skip
reconciliation.

### Realm settings

The managed realm-level settings are the realm's display and theme, the registration and login
switches (the password reset link among them), password and brute-force policy, token
lifetimes, and SSO timeouts. They include the flow bindings: which flow runs a browser sign-in,
a registration, a direct grant, a password reset, a client's authentication, and a brokered
first sign-in. `realms.json` names Keycloak's built-in flows for each, so a binding re-pointed
at another flow, by hand or by a mistaken import, is pointed back. Only the binding is
reconciled, not the built-in flows themselves.

They also include the event settings: user and admin events on, kept for 90 days, and admin
events without the representation that was sent, because a representation can carry a client
secret or a user's initial password and the event store keeps it for the whole 90 days. See
[Sign-in and admin events](deployment.md#sign-in-and-admin-events) for where to read them.

### Where web-app may send a browser back to

The `web-app` client's `redirectUris`, `webOrigins` and post-logout redirect URIs are
reconciled, because a realm created while they were wildcards otherwise keeps them as
wildcards. `realms.json` writes them as `${WEB_HOSTNAME}/signin-oidc` and
`${WEB_HOSTNAME}/signout-callback-oidc`, with no web origins. Nothing else about `web-app` is
reconciled.

Deployed, `WEB_HOSTNAME` is the public origin, and Compose fills it into the imported file
itself. Locally, the AppHost sets it on Keycloak and on the worker to the web app's endpoint as
the browser sees it: HTTPS under every launch profile but the AppHost's plain `http` one, and
on `localhost` rather than the address containers use to reach the host, which a first-boot
import would otherwise have stored as a redirect URI no browser sends. The port is not fixed
(the AppHost tests choose their own on every run), so the worker applies it on every start,
not only the first. Keycloak refuses a
sign-in from any other origin, including the web app's plain HTTP endpoint when it also has
HTTPS. If the variable is missing, the worker fails rather than store the placeholder.

A realm with no `web-app` client fails the run and points at the realm reset. The worker does
not create it, because its redirect URIs are the environment's, and creating it would make the
worker a second importer.

### The cli client

The worker manages the `cli` client that `groupsplit auth login` signs in through: the device
authorization grant on and every other grant off (authorization code, implicit, password,
service account and CIBA), public, and an audience mapper that puts `api` in its tokens.
Without that mapper the API refuses every token the CLI brings. Unlike `web-app`, a missing
`cli` client is created, from its definition in `realms.json`. A realm imported before the CLI
existed has none, and nothing in the client depends on the environment, so creating it writes
what an import would have.

On the audience mapper, only the config entries the file names are compared. Keycloak adds
entries of its own to a mapper over time, and those are left alone rather than fought over on
every run.

### Which account a first Google sign-in lands in

The `google` provider's `trustEmail`, its first broker login flow, and its essential-claim
filter are reconciled, and so is that flow, which is brought into line before the provider is
pointed at it. The flow is rebuilt whole if it differs at all. It runs two steps: find the
account that holds the email Google sent, then link to it. Before them there is no profile
form, because the email field on that form was what got looked up, and typing somebody else's
address there linked your Google account to theirs. The filter makes Keycloak refuse any Google
ID token whose `email_verified` is not `true`, so an address Google never checked cannot match
an account either.

A sign-in that arrives mid-rebuild meets a flow that is empty or short of its last step. It
ends with no account chosen and fails; it cannot land in the wrong account.

The provider's switch, client ID and secret stay with the container. A realm without a
`google` provider is not given one: its credentials are the container's, and a realm without
the provider has nobody signing in through it.

### The account-deletion client

The worker owns one client whole: `account-deletion`. Unlike `web-app` it is created when the
realm lacks it, and on every run it is held to the file's flows, the deploy secret and exactly
the file's roles. The client, and why the worker holds it this way, are described under
[The Keycloak client it uses](#the-keycloak-client-it-uses).

### The admin realm

When it is given `Realm__AdminConsoleUrl`, which only a deployment does, the worker sets the
`master` realm's frontend URL to it, so the admin console signs in through the SSH tunnel. See
[Reaching the deployed admin console](deployment.md#reaching-the-deployed-admin-console).

### What it does not manage

The worker does not manage SMTP settings, the rest of each client and provider, protocol
mappers other than the `cli` audience, the built-in authentication flows, roles, or users.
Those stay with realm import and the seeder. The allowlists in
[`KeycloakRealmDefinition`](../src/GroupSplit.Keycloak/KeycloakRealmDefinition.cs) draw that
line.

### Resetting the local realm

To restore the complete local realm to its checked-in first-boot definition, use "Reset local
Keycloak realm" on the `realm-settings` resource in the Aspire dashboard. It runs the
`realm-reset` worker, which deletes the realm and its users, then restarts Keycloak and lets
`--import-realm` recreate clients, flows and the rest from `realms.json`. If the delete fails,
Keycloak is not restarted and the command says to read the `realm-reset` log. The demo
accounts go with the realm; start the seeder again to get them back. The command exists only
in local run mode. A deployed realm is never reset or imported again, which is what the worker
is for.

## Changing the realm

Before editing `realms.json`, decide how the change will reach the realms that already exist.

- A managed setting reaches every existing realm on its next start or deploy. Anything else
  reaches a fresh import and nothing else: reset the local realm to see it, and in production
  make the same change in the admin console, or add the setting to the allowlist in
  `KeycloakRealmDefinition`. Adding it there is checked: loading the definition fails when
  `realms.json` lacks a managed setting, client property, attribute or mapper.
- A managed authentication flow may hold only top-level authenticators with a requirement, no
  subflows and no authenticator configs. The worker rebuilds such a flow in a handful of calls;
  anything nested would take most of an importer.
- A new `${...}` placeholder has to name an Aspire parameter in screaming-snake form, and the
  parameter needs to be added to the deploy workflow's allowlist; see
  [Deployment parameters](deployment.md#deployment-parameters). If a managed setting uses it,
  set it on Keycloak with `WithRealmVariable` so the worker is handed the same value; Keycloak
  resolves it on a first-boot import and the worker on every start after, and the two must
  never disagree.
- The session timeouts move with the web app's cookie lifetime or not at all; see
  [How long a session lasts](deployment.md#how-long-a-session-lasts).

`RealmDefinitionTests` in `tests/GroupSplit.Seeder.Test/Realm` reads the file and holds it to
the rules above: no client accepts a wildcard redirect, Google links only on an address Google
verified, every flow binding is named and reconciled, the password reset follows the mail
switch. `RealmSettingsReconcilerTests` beside it covers the worker. The tests in
`tests/GroupSplit.AppHost.Test/Web` whose names start with `Realm` run the worker against a
real Keycloak. How to run each is in [testing.md](testing.md).
