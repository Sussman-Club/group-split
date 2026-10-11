# Deleting an account

`DELETE /users/me` -- the **Delete my account** button on the Account page, or
`groupsplit users delete` -- deletes the account in two places: the app's own database, and
Keycloak, where the login lives. This page is the decision about what that means, and how the
two halves are kept from disagreeing.

## The decision: the Keycloak user is deleted

Decided in [#360][360]. Deleting an account deletes its Keycloak user. It is not disabled.

The alternative was to disable the user and remove its Google link, which would have kept a
way back: an operator could re-enable it. It was turned down because a person who deletes their
account is asking for their data to be erased, and the Keycloak user is most of that data --
their name, their email address, their password hash, their Google link, their sessions. A
disabled user still holds all of it, indefinitely, for a restore nobody asked for. Nothing in
the app needs the user afterwards either: the app's own row is what the ledger points at.

Before this, deleting an account left the Keycloak user untouched, and that showed:

- **The person could still sign in.** Their next request found no account for their subject,
  and the app quietly made them a new, empty one. From their side, deleting the account looked
  like it had not worked.
- **Their identity data stayed in Keycloak:** name, email, password hash, Google link, sessions.
- **Their sessions stayed alive** until they expired -- up to 30 days for a remembered one.

## What the person loses, and what stays

| Gone | Stays |
| --- | --- |
| Their name and email address on the app's row. | The row itself, marked deleted and anonymised. Every other member's balance is calculated from the expenses and settlements it took part in, so deleting the row would rewrite other people's ledgers. Those records now show a deleted account. |
| Their membership of every shared group. | The expenses and settlements they recorded, in the groups they were recorded in. Their personal group stays with the anonymised row: it has no balance and nobody to leave it to. |
| Their Keycloak user: login, password, profile, Google link. | The Keycloak subject (an opaque id) on the anonymised row, as a tombstone -- see below. It names nobody once the user it identified is gone. |
| Every Keycloak session: the web app's, the CLI's refresh token, any other device. | |
| The web app's own session in the browser they deleted from. | |

It cannot be undone. Signing up again with the same address (by an admin, since registration
is closed) makes a new login and a new, empty account; it does not reach the old one.

Deleting is refused with `ACCOUNT_NOT_SETTLED` while any shared group's balance is not zero,
and then nothing changes in either place.

## How it is carried out

1. **The app deletes its half, in one save.** The row is anonymised and marked
   `Deleted`, its memberships end, and a `PendingLoginDeletion` row is written -- all in the
   same transaction, so the login deletion is never owed without the account having gone, nor
   the other way round.
2. **The same request then deletes the login**, through Keycloak's admin API: it ends the
   user's sessions (`POST /users/{id}/logout`) and deletes the user (`DELETE /users/{id}`).
   On success the pending row is removed. This attempt waits at most ten seconds.
3. **The web app signs the person out locally** and shows the sign-in page with a note that
   the account and its sign-in were deleted. Locally only: the Keycloak sessions went with the
   user, and a Keycloak sign-out would only ask Keycloak to end a session it no longer has. The
   CLI throws away its stored credentials.

Deleting a Keycloak user removes its sessions and offline sessions, so step 2's sign-out is the
belt to that brace. It is the documented way to end sessions, and it runs first because after
the delete it would only answer 404. Either step finding no user counts as done, so a retry
after an unreported success is harmless.

### When Keycloak cannot be reached

The account is deleted regardless; it was saved before Keycloak was asked. What is left is a
login that should not exist, and two things keep that from becoming a silent mismatch:

- **A durable retry.** The `PendingLoginDeletion` row stays. `SweepPendingLoginDeletions` runs
  every five minutes and tries every row older than a minute again, until Keycloak confirms.
  Each failure is logged with the subject and the attempt count (a warning at first, an error
  from the third attempt on), and it never gives up, because giving up would leave the login
  standing for somebody who asked for it to go.
- **The login is refused in the meantime.** The anonymised row keeps its subject, so a sign-in
  with that login finds a deleted account rather than none. `UserProvisioner` refuses it with
  403 `ACCOUNT_DELETED`; it never provisions a fresh account in its place. The same refusal
  covers an access token issued before the deletion, which stays valid until it expires.

The person is not told their login still exists, because to them it does not: nothing will
sign in as it, and it is gone within minutes of Keycloak coming back.

## The Keycloak client it uses

The API never signs in as an administrator of the master realm. It uses `account-deletion`, a
confidential client in the `group-split` realm:

- client credentials only -- standard flow, implicit flow and direct access grants off;
- its service account holds `realm-management`'s `manage-users` and nothing else. That covers
  deleting a user and ending their sessions. `view-users` is not granted: the API never reads
  a user, and both calls it makes are `manage-users` operations;
- its secret is the `keycloak-account-deletion-secret` parameter.

It is defined in [`realms.json`](../src/GroupSplit.AppHost/Assets/keycloak/realms.json), so a
fresh realm imports it. A realm that already exists (production's, and every developer's,
which live on volumes and are never imported again) gets it from the `realm-settings`
worker, which creates the client when it is missing and on every run holds it to the file:
the flows above, the secret it was given, and exactly the roles the file lists, revoking any
added by hand. So there is no manual step on an existing realm. The API waits for that worker,
so it never starts with a secret Keycloak does not know.

Locally the secret is generated and kept in the AppHost's user secrets. Deployed it is the
`KEYCLOAK_ACCOUNT_DELETION_SECRET` secret on the `production` environment -- see
[deployment parameters](development-and-deployment.md#deployment-parameters).

**Rotating it** is changing that secret and deploying. The worker writes the new value to the
client, then the API starts with it. Between the worker and the API restarting, the old API
cannot delete logins; anything it tried is retried by the sweep.

## Accounts deleted before this

Accounts deleted before this change kept their Keycloak user and lost their subject here, so
nothing in the app records which login was theirs, and some of those people have signed in
since and own a fresh account. Those logins are not deleted automatically. Finding the ones
nobody uses is an operator's job at the admin console.

[360]: https://github.com/Sussman-Club/group-split/issues/360
