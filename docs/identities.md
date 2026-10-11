# Sign-ins and accounts

A `User` row is the person: their name, their shares, their place in other people's
balances. A `UserIdentity` row is the sign-in that answers to it -- Keycloak's `sub`, opaque
here. [`UserProvisioner`](../src/GroupSplit.API/Services/UserProvisioner.cs) joins the two on
every authenticated request: it finds the row by `sub`, makes one on first sign-in, and copies
the name and email from the token onto it.

`User.Email` is unique. That is where the `sub` and the address can disagree.

## A new sign-in with an address another row holds

**What happens.** Somebody is deleted in Keycloak and created again with the same address --
by an admin, or by signing up again after an admin removed them. Keycloak gives them a new
`sub`. The app has never seen it, so it would make a new row, and the old row still holds the
address. Every request is refused with `409 IDENTITY_EMAIL_IN_USE`
([errors.md](errors.md)). The web app shows "We could not open your account" with a
reference; the CLI exits `2` and says to contact support.

**Why not link it automatically.** Owning an address in Keycloak today is not proof of being
the person whose expenses are on the old row, and linking would hand that row (its balances,
its groups, its bank links) to whoever holds the new sign-in. That is a decision for a
person to make, once, with the facts in front of them.

**What support does.**

1. Find the log line by the reference (the trace id) the person quotes. It is a warning:
   `IDENTITY_EMAIL_IN_USE: sign-in <new sub> is new, and its email is already on user <holder id>`.
2. Read the holder's old `sub` from the database
   (`SELECT "IdentityId" FROM "UserIdentity" WHERE "UserId" = '<holder id>'`), look it up in
   the Keycloak admin console ([reaching it](development-and-deployment.md#reaching-the-deployed-admin-console)),
   and decide which case it is:

   - **The same person, recreated.** The old `sub` no longer exists in Keycloak, and the person
     confirms -- out of band, not on the strength of the address -- that the old account was
     theirs. Point the old row at the new sign-in, so they keep their history:

     ```sql
     UPDATE "UserIdentity" SET "IdentityId" = '<new sub>' WHERE "UserId" = '<holder id>';
     ```

     If the holder has no `UserIdentity` row at all, insert one with a new `Id`, that
     `UserId` and the new `sub`. Their next request goes through as the old row.

   - **The same person, who wants a fresh start**, or the old row belonged to somebody else who
     is gone. Free the address on the old row and leave the row itself alone (other members'
     balances are made of it, which is also why deleting an account in the app clears the
     email and keeps the row):

     ```sql
     UPDATE "User" SET "Email" = NULL WHERE "Id" = '<holder id>';
     ```

     The next request makes a new, empty row for the new sign-in.

   - **Two different people, both still in Keycloak.** The old `sub` still exists and has the
     same address (a realm that allows duplicate emails, or an address moved between accounts).
     Nothing changes in the database: correct the address on the wrong Keycloak account, and the
     refusal stops on the next request.

Never delete the `User` row, and never point two identities at one row.

## An address change onto one another row holds

**What happens.** Somebody changes their email in Keycloak to an address another row holds.
They are already known by their `sub`, so they are not refused: the request goes on, the rest
of the profile still follows Keycloak, and the stored address stays as it was. A warning is
logged on every such request:
`IDENTITY_EMAIL_IN_USE: user <id> (sign-in <sub>) has an email in Keycloak that another user holds`.

**Why not refuse it.** The address is only displayed. Refusing would lock somebody out over
it, for good when two people swap addresses, since each would be waiting on the other.

**What support does.** Usually nothing: the next request tries again, so it resolves by itself
once the other row lets go of the address. A swap never does. Free one side and let the next
requests settle it:

```sql
UPDATE "User" SET "Email" = NULL WHERE "Id" = '<one of the two>';
```

The other person's next request takes the freed address, and then the first person's takes
theirs.
