# Group Split

Group Split is an expense-sharing app for groups of people.
It helps groups track shared expenses, apply split rules, and settle balances with fewer manual calculations.

## What it does

- Create groups and invite people to them by email
- Record shared and personal expenses, split however you like
- Split costs by category default, or state the shares on one expense
- Calculate per-member balances, and settle up from either side
- See where you stand across every group at once
- Link a bank and review what it sends before any of it becomes an expense
- Be told when a bank transaction is an expense somebody already recorded, and attach it to
  that one instead of counting the money twice
- Support web and mobile clients backed by a single API

## Project structure

`group-split.slnx` groups projects into solution folders by the role they play. Those
folders are how the solution reads in an IDE; on disk the projects stay flat under
`src/`, at the paths listed below.

### src — API and contracts

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.API` | ASP.NET Core API for groups, invitations, categories, split rules, transactions, users, and bank data |
| `src/GroupSplit.Jobs` | The background-job seam -- `IJobDispatcher`, `IJobHandler<T>`, `IJobReceiver` -- plus the in-memory transport and recurring scheduler it defaults to. A queue service and a function replace the transport; no handler changes |
| `src/GroupSplit.Shared` | DTOs and validation shared between the API and the clients |

### Aspire — orchestration

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.AppHost` | .NET Aspire app model for local development and publishing; see [docs/development-and-deployment.md](docs/development-and-deployment.md) |
| `src/GroupSplit.Seeder` | Worker, launched by the app model, that seeds reference and demo data |
| `src/GroupSplit.ServiceDefaults` | Shared telemetry, health check, and service discovery defaults |

### Clients — front ends

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.App/GroupSplit.App` | .NET MAUI client |
| `src/GroupSplit.App/GroupSplit.App.Web` | Blazor Web host |
| `src/GroupSplit.App/GroupSplit.App.Web.Client` | Blazor WebAssembly client |
| `src/GroupSplit.App/GroupSplit.App.Shared` | Razor class library with the UI both clients share |
| `src/GroupSplit.Cli` | `groupsplit`, the terminal client |

### Data — persistence

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.Data` | Entities and the provider-agnostic `DbContext` |
| `src/GroupSplit.Data.PostgreSQL` | PostgreSQL provider wiring |
| `src/GroupSplit.Data.PostgreSQL.Migrations` | EF Core design-time project holding the migrations |

### tests

| Project | Purpose |
| --- | --- |
| `tests/GroupSplit.API.Test` | API endpoint tests |
| `tests/GroupSplit.App.Web.Test` | Blazor Web host tests |
| `tests/GroupSplit.AppHost.Test` | Aspire orchestration and integration tests |
| `tests/GroupSplit.Cli.Test` | CLI configuration, output contract and auth tests |

## Splitting

A category points at a split rule -- evenly, by percentages, by whole shares, or all on
whoever paid -- and an expense filed under it is divided that way unless the shares are
stated on the expense itself. A rule is a template and not a record: an expense stores the
amounts it was divided into, so editing a rule changes what the next one is pre-filled with
and nothing already recorded.

Rules name people, and people join groups and leave them. What a stored rule means then --
who is taken out of it, what happens to their share, and why a percentage rule can be left
reading less than 100 -- is in
[docs/split-rules-and-membership.md](docs/split-rules-and-membership.md).

## Errors

Every non-2xx API response is RFC 9457 problem details carrying a stable `code`, a `traceId`
and, where it helps, extension members naming what is in the way. The contract, the code
catalog and how each side produces and consumes it are in [docs/errors.md](docs/errors.md).

## Command line

`groupsplit` talks to the same API as the apps, from a terminal or from a script. No server
URL is compiled in -- `groupsplit config set server <url>`, `--server` or `GROUPSPLIT_SERVER`
picks one at runtime. Sign-in is the OAuth 2.0 device flow, so it works over SSH and in
containers; `GROUPSPLIT_TOKEN` skips it entirely for CI.

It installs as a .NET tool from GitHub Packages, private to the org, so it needs a
`read:packages` token but no checkout: point NuGet at the feed once, then
`dotnet tool install --global GroupSplit.Cli` and `dotnet tool update --global GroupSplit.Cli`.
Merging to `main` publishes it, the same way that deploys the stack. Both are in
[docs/cli.md](docs/cli.md).

It renders tables for a person and JSON for anything else, keeps stdout to the result alone,
and reports failures as an envelope carrying the API's own error `code`. `groupsplit schema`
prints the whole command tree as JSON for callers that cannot read help text. The full
contract -- output, exit codes, the confirmation protocol -- is in [docs/cli.md](docs/cli.md).

Agents are told that contract before their first command rather than after their first
mistake: `skills/` holds two agent skills -- the CLI protocol, and the bank-row
reconciliation loop -- installable with `npx skills add Sussman-Club/group-split` or as a
Claude Code plugin. See [skills/README.md](skills/README.md).

## Design system

One palette, one type pair, one set of radii and shadows -- shared by the app, the Keycloak
login pages, the Keycloak account console and the mail Keycloak sends. Nothing fans a token
out at build time, so **a style change is not finished until it is in every view that shows
it.** The files that each carry a copy, and how to check a change in both light and dark,
are in [docs/design-system.md](docs/design-system.md).

## Run locally

```bash
aspire start
```

`dotnet run --project src/GroupSplit.AppHost` does the same from an IDE. Either way the
AppHost is in run mode: it orchestrates the stack itself and adds the development
tooling (Mailpit, the seeder, Scalar, the Postgres MCP servers) that a deployment never gets. How that
differs from what ships, and what a deployment has to be told, is in
[docs/development-and-deployment.md](docs/development-and-deployment.md).

## Demo accounts

The seeder does not start on its own. Start the `seeder` resource from the dashboard, or use
its **Reset databases and seed** command to start from scratch, and it fills both the app
database and the Keycloak realm from
[`SeedData/users.json`](src/GroupSplit.Seeder/SeedData/users.json). You can then sign in as
any of them:

| Email | Password |
| --- | --- |
| `daniel@test.com` | `GroupSplit123!` |
| `anabel@test.com` | `GroupSplit123!` |
| `loraine@test.com` | `GroupSplit123!` |
| `omar@test.com` | `GroupSplit123!` |

The password comes from `Keycloak:DefaultPassword` in the seeder's
`appsettings.Development.json`; a seed entry may name its own instead.

Both halves come from the same file and share one id: the seeder creates each Keycloak
account with the entry's `ExternalUserId`, which is the identity id the database row is
linked by. That is what makes signing in land on the seeded groups and expenses. It matters
because the API links an account by the token's subject and provisions a new one for a subject
it has not seen -- so an account **registered by hand** carries a subject the seed data has
never heard of, the API tries to create a second account with the same address, and every
request fails on the unique email index. Meeting that case, the seeder replaces the
hand-made account and says so in its log; set `Keycloak:ReplaceConflictingUsers` to `false`
to be warned and left alone instead.

Two things worth knowing:

- Keycloak keeps its users in a data volume, so the accounts outlive a restart and reruns of
  the seeder leave them alone. Resetting the app database without resetting Keycloak is fine:
  the ids are fixed in `users.json`, so the two ends still agree.
- The accounts are created only in local run mode. `realms.json` deliberately holds no users:
  it ships to deployments, and realm import only runs when the realm does not yet exist.

## Email

Keycloak sends the password reset and email verification mail, so it needs an SMTP relay.

Locally there is nothing to configure. The AppHost runs [Mailpit](https://mailpit.axllent.org/),
every message Keycloak sends lands in its inbox, and nothing leaves the machine -- open the
inbox from the `mailpit` resource in the Aspire dashboard.

In production the relay comes from deployment values set on the `production` GitHub
environment. `SMTP_PASSWORD` is a secret; the rest are variables:

| Name | Example | Purpose |
| --- | --- | --- |
| `SMTP_ENABLED` | `true` | The switch. Off unless set, and then the four below are required |
| `SMTP_HOST` | `smtp.resend.com` | Relay hostname |
| `SMTP_PORT` | `587` | Optional, defaults to 587 (submission over STARTTLS) |
| `SMTP_FROM` | `no-reply@example.com` | Sender address, on a domain the relay has verified |
| `SMTP_USER` | `resend` | SMTP username |
| `SMTP_PASSWORD` | | SMTP password or API key (secret) |

Leave `SMTP_ENABLED` unset and the deploy still succeeds: Keycloak simply cannot send
mail. Setting it to `true` with any of the four missing fails the deploy deliberately,
because a realm that offers password reset over a relay that rejects every send is worse
than one that never offered it. The check runs twice: once in the workflow before
anything is built, and once in the AppHost's `validate-smtp` pipeline step once the
parameters are resolved, so a publish from a laptop gets it too.

The realm does not require registrations to verify their address. Having a relay is not
the same as trusting it: a sender domain part way through verification at the provider
has every send rejected, and a verification requirement then strands users at their next
login behind a mail that cannot arrive. Switch `verifyEmail` on in `realms.json` once
mail is really flowing, if you want it at all.

Google sign-in follows the same shape: `GOOGLE_SIGN_IN_ENABLED` is the variable that
switches it on, and it then requires the `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`
secrets.

Whichever relay you use, expect to prove you own the sender domain by adding the SPF and
DKIM records it gives you. Mail from an unverified sender is rejected or filed as spam.

### Why the variable names have to match

Publishing does not mount `realms.json` as a file. The Compose publisher inlines its
text into a Compose config's `content`, and Compose interpolates `${...}` inside that
text against its own environment file before Keycloak ever reads it. So every
placeholder in `realms.json` has to spell a name that file defines -- which is the
screaming-snake form of the Aspire parameter it comes from, so `smtp-from` pairs with
`${SMTP_FROM}`.

A name Compose cannot resolve is replaced with a blank string and nothing complains
except a warning in the deploy log. That matters most for the sender address: Keycloak
validates it while importing the realm and **refuses to start** on one it cannot parse,
an empty string included. Hence a valid unroutable default for the unconfigured case
rather than an empty one.

### Applying this to a realm that already exists

`realms.json` is the source of truth, but `start --import-realm` only ever creates a realm
that is not there yet -- it skips one the database already holds. A realm created before
these settings existed therefore will not pick them up. Either reset the Keycloak database
so the realm is imported afresh, or set the values once by hand under Realm settings ->
Email in the admin console, which also has a "Test connection" button worth using.

## Bank sync

People can link a bank and have its transactions arrive in an inbox, where each one is
added to a group, kept personal, or ignored. Nothing imported becomes an expense on its
own: an imported row is staging data until somebody files it, and filing copies the bank's
figures into an ordinary expense and links the two.

The provider sits behind `IBankConnector`, and `PlaidConnector` is the only class that
speaks Plaid. Bank sync is available exactly when a connector is registered, which happens
when the deployment has credentials -- so an app with none starts as usual and says bank
sync is off rather than offering a button that cannot work.

Locally, Plaid's sandbox needs no approval and opens any institution with `user_good` /
`pass_good`; the credentials go in the AppHost's user secrets. Webhooks are the one part
that cannot work on a laptop, because Plaid has to reach the app from outside, so a sync
runs when the app asks for one and a daily sweep catches the rest.

The access tokens are encrypted with ASP.NET Data Protection, whose key ring is itself
encrypted with a certificate the deployment holds as a secret. A key ring sitting in the
same database as the ciphertext would protect it from a leak of one table and from nothing
else, so a deployment with bank sync on and no certificate is refused at publish time. See
[the key ring](docs/development-and-deployment.md#the-bank-access-token-key-ring).

How it is put together, and what was deliberately left out, is in
[docs/phase-3-plaid-integration.md](docs/phase-3-plaid-integration.md); the deployment
parameters are in
[docs/development-and-deployment.md](docs/development-and-deployment.md).

## Test

```bash
dotnet test tests/GroupSplit.API.Test
dotnet test tests/GroupSplit.AppHost.Test
```

## Tech stack

- .NET 10 (ASP.NET Core, Blazor, MAUI)
- PostgreSQL + Entity Framework Core
- .NET Aspire for local orchestration
