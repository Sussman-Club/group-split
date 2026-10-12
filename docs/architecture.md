# Architecture

Group Split is one ASP.NET Core API with three clients in front of it: a Blazor web app, a
.NET MAUI app and `groupsplit`, the command-line client. One Aspire AppHost describes the
whole stack, both on a developer's machine and on the Docker host a deploy ships it to.
This page says which project holds what, how a request gets from a client to the database,
and which decisions behind the model still constrain a change. Running the stack is in
[getting-started.md](getting-started.md), the test suites in [testing.md](testing.md), and
shipping it in [deployment.md](deployment.md).

## The parts

`group-split.slnx` groups the projects into solution folders by the role they play. The
folders are virtual: on disk every project sits flat under `src/` or `tests/`, at the paths
below.

### src: the API and its contracts

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.API` | The HTTP API: groups, guests and links, categories, split rules, transactions, receipts, merchants, users, and bank data |
| `src/GroupSplit.Shared` | DTOs, validation attributes, error codes and the JSON serializer settings shared by the API and every client; also the NSwag templates the clients are generated from |

### src/Aspire: orchestration

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.AppHost` | The Aspire app model, for local runs and for publishing; see [The AppHost](#the-apphost) |
| `src/GroupSplit.Seeder` | Worker the AppHost launches on request to seed demo data into the app database and the Keycloak realm |
| `src/GroupSplit.RealmWorker` | Worker the AppHost runs as `realm-settings`, which reconciles the Keycloak realm with `realms.json`; locally it also resets the realm on request |
| `src/GroupSplit.Keycloak` | Keycloak admin client and the realm definition read from `realms.json`, shared by the API, the seeder and the realm worker |
| `src/GroupSplit.ServiceDefaults` | Telemetry, health check, resilience and service discovery defaults every service takes |

### src/Clients: front ends

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.App/GroupSplit.App` | .NET MAUI client |
| `src/GroupSplit.App/GroupSplit.App.Web` | Blazor Web host: serves the UI, signs people in, and forwards to the API and Keycloak |
| `src/GroupSplit.App/GroupSplit.App.Web.Client` | Blazor WebAssembly client |
| `src/GroupSplit.App/GroupSplit.App.Shared` | Razor class library holding the UI all three hosts render, and the generated API clients |
| `src/GroupSplit.Cli` | `groupsplit`, the terminal client; see [cli.md](cli.md) |

### src/Data: persistence

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.Data` | Entities and the provider-agnostic `AppDbContext` |
| `src/GroupSplit.Data.PostgreSQL` | `PostgreSqlAppDbContext`, holding what only PostgreSQL can express, and its registration |
| `src/GroupSplit.Data.PostgreSQL.Migrations` | EF Core design-time project holding the migrations |

### src/Jobs: background work

| Project | Purpose |
| --- | --- |
| `src/GroupSplit.Jobs` | The background-job seam (`IJobDispatcher`, `IJobHandler<T>`, `IJobReceiver`, `IJobScheduler`) and the in-memory transport and scheduler it defaults to |

### tests

| Project | Purpose |
| --- | --- |
| `tests/GroupSplit.API.Test` | API endpoint and service tests, on EF's in-memory provider |
| `tests/GroupSplit.App.Web.Test` | Blazor Web host and component tests, with bUnit |
| `tests/GroupSplit.AppHost.Test` | Aspire orchestration and integration tests against the whole stack in Docker, driven with Playwright |
| `tests/GroupSplit.Cli.Test` | CLI configuration, output contract and auth tests |
| `tests/GroupSplit.Jobs.Test` | Background-job configuration, processing and scheduling tests |
| `tests/GroupSplit.Seeder.Test` | Seeder, Keycloak admin client and realm worker tests |

Two scripts sit beside them and run in CI: `tests/check-css.py` checks the shared CSS bundle
for duplicate selectors, and `tests/coverage-gate.py` fails the build when merged coverage
drops below its floors.

### Who references whom

The API references `GroupSplit.Data.PostgreSQL` (and through it `GroupSplit.Data`),
`GroupSplit.Jobs`, `GroupSplit.Keycloak`, `GroupSplit.Shared` and
`GroupSplit.ServiceDefaults`. The seeder references the API itself, so it divides expenses
with the same `SplitCalculator` and split-rule handlers the API does.

`GroupSplit.App.Shared` and `GroupSplit.Cli` also reference the API project, but for its
OpenAPI document and not its assembly (`ReferenceOutputAssembly="false"`). Building the API
writes `obj/Generated/GroupSplit.API_api.json`, and NSwag turns it into typed clients using
the templates in `src/GroupSplit.Shared/ClientTemplates`. The DTOs are not generated; both
sides use the ones in `GroupSplit.Shared`. A changed endpoint therefore reaches the web app
and the CLI on their next build, and a changed DTO is one edit.

## The AppHost

[`AppHost.cs`](../src/GroupSplit.AppHost/AppHost.cs) is the whole model, the way Aspire
expects: resources added with `Add*`, shaped with `With*`, wired with `WithReference` and
`WaitFor`. Every mode shares Postgres with two databases (`db` for the app, `keycloak-db`),
RustFS storage with the `receipts` bucket, Redis for the web app's sessions, Keycloak, the
`realm-settings` worker, the EF migrations, the API and the web app, plus Veryfi, Plaid and
Azure OpenAI as external dependencies of the API.

The start order is the wiring. The API waits for the database, storage and Keycloak, and for
the migrations and the realm worker to finish successfully; the web app waits for the API
and the cache.

### Two axes, not one

Aspire keeps two independent questions apart, and so does this repo:

| Question | Values | Read from |
| --- | --- | --- |
| How was the AppHost invoked? | run mode (`aspire start`, `dotnet run`, the test host) or publish mode (`aspire publish`, `aspire do`, `aspire deploy`) | `builder.ExecutionContext.IsRunMode` |
| Which deployment is this for? | `production` today; any name works | `--environment <name>` on the deployment commands |

The AppHost branches on the first. The second only decides which parameter values are
loaded and what the generated env file is called (`.env.production`).

### What lives where

After the shared resources, one `IsRunMode` check splits the model into two blocks. Each is
short, because a resource that has a different shape in each mode carries that shape as a
pair of extension methods next to the code that owns it:

| Resource | Run mode | Publish mode |
| --- | --- | --- |
| Keycloak | [`AsDevelopmentKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDevelopmentExtensions.cs): realm and theme mounted from the checkout, Mailpit as the relay, a data volume | [`AsDeployedKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDeploymentExtensions.cs): served under the public origin at `/idp` behind the web app's forwarder, realm and theme shipped as Compose configs, a Compose healthcheck; plus [`WithSmtp()`](../src/GroupSplit.AppHost/Extensions/SmtpExtensions.cs) for the relay |
| Realm settings | `realm-settings` waits for Keycloak, then reconciles the managed settings from the realm export; `realm-reset` backs the dashboard's reset command | The same worker runs as a one-shot Compose service, signed in as a named admin; the API starts only after it exits successfully |
| Postgres | Aspire's `WithPostgresMcp` on each database; DBX and a `psql` REPL in the dashboard's terminal dock on the server, as on the Redis cache | [`AsDeployedPostgres`](../src/GroupSplit.AppHost/Extensions/PostgresDeploymentExtensions.cs): the database-creation script the orchestrator would otherwise have run, a Compose healthcheck |
| Storage | `AddBucket` on the RustFS server; the orchestrator creates the `receipts` bucket once the server is up | [`AsDeployedRustFs`](../src/GroupSplit.AppHost/Extensions/RustFsDeploymentExtensions.cs): wraps the entrypoint in a script, shipped as a Compose config, that creates the bucket over S3, and a healthcheck that passes only once it exists; `WithRustFsConsole` turns the console on, reachable only inside the Compose network |
| API and web | [`WithHealthEndpoints`](../src/GroupSplit.AppHost/Extensions/HealthEndpointExtensions.cs): an unpublished management endpoint with liveness and readiness probes | `WithManagementHealthcheck`, in the same file: the readiness probe restated as a Compose healthcheck against that endpoint |
| Compose | not present | `AddDockerComposeEnvironment` with [`WithProtectedDashboard`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs), the image registry, `WithComposeDefaults`, and `WithPrepareOnPush`, which makes `aspire do push` also write the Compose artifacts |

Only run mode adds the MAUI head, the demo seeder, the CLI, the local realm reset, Scalar,
Mailpit and the webhook tunnel. Publish mode adds the deployment-only parameters listed in
[Deployment parameters](deployment.md#deployment-parameters); the integration parameters
(Plaid, Veryfi, Azure OpenAI, Google sign-in) are declared in both. `PublishAs*` calls,
which Aspire ignores in run mode, stay on the shared chain where they read as part of the
resource's definition.

The rule for adding something new: if a deployment would never run it, it goes in the run
block; if it only makes sense once there is no AppHost around to orchestrate, it goes in the
publish block; otherwise it is shared. A resource that needs more than a line or two in
either block gets an `AsDevelopment*` / `AsDeployed*` pair in its own file under
`Extensions/` rather than a longer block. Files shipped into containers (the realm, the
Keycloak theme, the Postgres init script, the RustFS entrypoint) live under `Assets/`. The
theme restates the app's design tokens rather than importing them, so a style change has to
be made in every view that shows it; see [design-system.md](design-system.md).

The generic Compose plumbing those methods lean on is in
[`Extensions/DeploymentExtensions.cs`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs):
shipping files as configs, healthchecks, the external network, the key-ring volume, and the
restart and `depends_on` defaults the publisher leaves to the operator.

## How a request travels

```text
browser ──cookie──▶ web (GroupSplit.App.Web)
                    ├─ /api/*          cookie in, session's token out      ──▶ api
                    ├─ /native/api/*   caller's token passed through       ──▶ api
                    ├─ /webhooks/*     anonymous; API checks the signature ──▶ api
                    ├─ /idp/realms/group-split/*, /idp/resources/*         ──▶ keycloak
                    └─ /auth/*         sign-in and sign-out (IdentityApi.cs)

web, rendering on the server ──token──▶ api   (service discovery, no forwarder)
groupsplit CLI ──token──▶ web /native/api ──▶ api
```

Deployed, the web app is the only thing with a public origin. The API and Keycloak publish no
port, so everything reaches them through the forwarders in
[`WebAppExtensions.cs`](../src/GroupSplit.App/GroupSplit.App.Web/WebAppExtensions.cs).
Locally the API and Keycloak have endpoints of their own, and the forwarders are there but
mostly unused.

### The web app

The UI is the Razor components in `GroupSplit.App.Shared`. The Web host renders them in one
of two modes, chosen by the `RenderMode` setting and applied by
[`RenderModeConfig.cs`](../src/GroupSplit.App/GroupSplit.App.Web/RenderModeConfig.cs):
`ServerFirst` (interactive Server), the default and so what a deployment runs, or
`WebAssemblyFirst`, which `appsettings.Development.json` sets locally. Which way a component
reaches the API depends on where it runs, and each side has its own `ClientOptionsSetter`:

- On the server (prerendering, or an interactive Server circuit), the generated clients call
  the API directly at `https+http://api`, and
  [`AuthDelegatingHandler`](../src/GroupSplit.App/GroupSplit.App.Web/Services/AuthDelegatingHandler.cs)
  puts the session's access token on each request.
- In WebAssembly, they call `/api/` on the page's own origin, carrying the
  `SameOriginRequest` header. The forwarder requires the cookie, refuses a write that cannot
  show it came from the app's own pages (`CROSS_SITE_REQUEST_REFUSED`), and swaps the cookie
  for the session's bearer token on the way through.

The browser never holds a token. Sign-in is OpenID Connect against Keycloak's `web-app`
client; the authentication ticket lives in Redis (`DistributedCacheTicketStore`) and the
tokens beside it in `ServerSideTokenStore`, kept fresh by
`Duende.AccessTokenManagement.OpenIdConnect`. Why the store is our own, and what refreshing
by hand gets wrong, is in
[Where the access token lives](deployment.md#where-the-access-token-lives); why the ticket is
in Redis is in [Sessions across a deploy](deployment.md#sessions-across-a-deploy).

### Blazor circuit startup and persisted state

Server-first rendering persists tracker data in the HTML, then sends that encrypted state
back over `/_blazor` when the interactive circuit starts. Home and Activity can exceed
SignalR's default 32 KiB incoming-message limit even when all their API requests succeed.
The page then looks loaded but stays inert, and the browser reports
`Connection closed with an error.` before a circuit is initialised. Groups can still work,
because its initial state is smaller.

`RenderModeConfig.cs` sets a 256 KiB limit on the Blazor hub, which leaves headroom over the
startup message of about 44 KiB seen in production. Other hubs keep their defaults. The
limit is per message: if tracker state grows past it, reduce the persisted data and keep the
limit.

To diagnose it, set `Logging__LogLevel__Microsoft.AspNetCore.SignalR` to `Debug` on the `web`
container for a while and read its logs in the Aspire dashboard. The rejection is logged as
`The maximum message size of ...B was exceeded`, which the usual production level hides. Put
the level back afterwards, and do not log the persisted state, authentication cookies or
circuit secrets. See Microsoft's
[maximum receive message size guidance](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0#maximum-receive-message-size).

### The CLI and the MAUI app

Clients that hold a token rather than a session use `/native/api`. The `/api` forwarder
cannot serve them: it authenticates with the cookie and then replaces the `Authorization`
header, so a bearer token arriving there is challenged and, past that, thrown away.
`/native/api` is anonymous at the web app and passes the header through; every endpoint group
in the API requires authorisation, so the API decides.

The CLI signs in with the device flow through Keycloak's `cli` client, and derives
`/native/api` and `/idp/realms/group-split` from one server URL in
[`EndpointResolver`](../src/GroupSplit.Cli/Configuration/EndpointResolver.cs). Locally the
AppHost's `cli` resource hands it the API and the realm separately (`GROUPSPLIT_API_URL`,
`GROUPSPLIT_AUTHORITY`), because there they are separate origins. See [cli.md](cli.md).

The MAUI app exists in run mode only and references the API directly. Its `AuthService` is
still a stub that throws, so it does not sign in yet; `/native/api` is the door it is meant to
use once it can be pointed at a deployment.

### Keycloak

Keycloak issues every token, from the `group-split` realm. The realm's first-boot definition
is [`realms.json`](../src/GroupSplit.AppHost/Assets/keycloak/realms.json), and the
`realm-settings` worker holds an existing realm to the managed parts of it on every start.
Deployed, only the app's realm and `/resources` go through the `/idp` forwarder; the admin
console and the `master` realm stay inside the network. The realm, its clients and the worker
are in [keycloak.md](keycloak.md).

### The API

[`Program.cs`](../src/GroupSplit.API/Program.cs) validates Keycloak's JWTs for the audience
`api`. [`CurrentUserMiddleware`](../src/GroupSplit.API/Middleware/CurrentUserMiddleware.cs)
then finds the `User` row the token's subject answers to, creating it (and its personal
group) on a first sign-in; what happens when a subject and an address disagree is in
[Sign-ins and accounts](keycloak.md#sign-ins-and-accounts).

Endpoints are minimal APIs, one file per area under `Endpoints/` (`GroupApi.cs`,
`TransactionApi.cs`, `SplitRulesApi.cs`, `InboxApi.cs` and so on), each a route group that
calls `RequireAuthorization`. They hand the work to services under `Services/`, registered
in [`DomainServiceExtensions.cs`](../src/GroupSplit.API/Extensions/DomainServiceExtensions.cs).
Bank sync is under `Services/Banking`, receipt reading under `Services/ReceiptTranscription`,
and the split-rule kinds under `Services/SplitRuleHandlers`.

## Data

The app database is PostgreSQL, through EF Core.

`AppDbContext` in `GroupSplit.Data` is provider-agnostic: the entities, their keys,
relationships, delete behaviours and indexes, with the reason for each beside it. Its only
`DbSet` property is `DataProtectionKeys`, which the Data Protection key repository reads by
name; everything else is reached through `Set<T>()`. Its `SaveChanges` also writes a
`GroupMembershipEvent` for every membership or guest that the save opens, closes or reopens,
so the history is written in the same transaction as the change.

`PostgreSqlAppDbContext` derives from it and adds what only PostgreSQL can say: partial
unique indexes (one current version per split rule, one unrevoked join link per group), the
check constraints that keep half-written departures, guest ends and user kinds out of the
tables, and the UTC conversion on transaction dates. `AddPostgreSqlAppDbContext` registers it
as `AppDbContext`, so services ask for `AppDbContext` and never name the provider. The API
tests run on EF's in-memory provider, which sees `AppDbContext`'s mapping and none of the
PostgreSQL additions; the AppHost tests run against a real database.

The migrations live in
[`GroupSplit.Data.PostgreSQL.Migrations/Migrations`](../src/GroupSplit.Data.PostgreSQL.Migrations/Migrations).
`AppContextPostgreSqlFactory` is the design-time factory for both context types: local
commands name `AppDbContext`, and a published migration bundle, which resolves the derived
type on its own, still finds a factory. A new migration is added with

```bash
dotnet ef migrations add <Name> --project src/GroupSplit.Data.PostgreSQL.Migrations --context AppDbContext
```

The AppHost applies them through its `migrations` resource. In run mode it runs the database
update when the AppHost starts; in publish mode it becomes a migration bundle in a container,
run as a one-shot Compose service that is not restarted. The API and the seeder wait for it
to complete, so neither ever runs against an old schema.

## Background jobs

`GroupSplit.Jobs` is a seam rather than a queue:

| Part | Does |
| --- | --- |
| `IJobDispatcher` | Submits a job now and returns a handle to its execution |
| `IJobHandler<TJob>` | Does the work for one job type |
| `IJobReceiver` | Yields deliveries from whatever transport is in use |
| `IJobScheduler` | Says when a job is submitted: `JobSchedule.Once`, `Every` or `Cron` |

The defaults are an in-memory queue drained by a hosted worker, and a scheduler that polls
every 100 ms. Both are process-local: a queued job or a schedule does not survive a restart.
A queue service replaces the transport by pointing the dispatcher and receiver at it; the
handlers do not change. Registration goes through `services.AddJobs()`, with handlers under
`.Handlers` and recurring jobs under `.Scheduler.Add(job, schedule)`. The details and the
in-memory policies are in [`src/GroupSplit.Jobs/README.md`](../src/GroupSplit.Jobs/README.md).

Everything on it today runs in the API:

| Job | Dispatched by | Does |
| --- | --- | --- |
| `SyncBankConnection` | A webhook, a link completing, a sync somebody asks for, the daily sweep | Imports one connection's new and changed rows |
| `SweepBankConnections` | Daily at midnight UTC | Dispatches a sync for every connection |
| `SweepPendingBankLinks` | Every five minutes | Finds bank links interrupted before they were stored |
| `CompletePendingBankLink`, `AbandonPendingBankLink` | The pending-link sweep | Finishes such a link, or gives it up |
| `SweepPendingLoginDeletions` | Every five minutes | Retries deleting the Keycloak login of an account already deleted here |

Each job carries an id and nothing else, and its handler reads the row afresh. A job that
runs late, twice, or after somebody else finished the work is then a no-op, and the sweeps
come back for anything a restart lost.

## The domain model

The entities are in [`src/GroupSplit.Data/Entities`](../src/GroupSplit.Data/Entities), and
their summaries say why each is shaped the way it is.

### Groups and the personal group

A `Group` is the unit every balance is computed within. A rule, a category and a transaction
each belong to exactly one, and nothing crosses a group: "what do I owe overall" is a sum
across groups. A group has a currency (ISO 4217), its split rules, its categories, a default
split rule, its guests and its join links.

Every account also has a personal group, whose id is the account's own (`Group.Id ==
User.Id`), with its owner as the only member. It is created with the account and is an
ordinary group in every other respect, so personal spending has categories, a page and
figures. A request that leaves the group out still means personal, and the response carries
the real id. Anything that needs a second person is refused with `PERSONAL_GROUP_NOT_SHARED`
by [`PersonalGroupGuard`](../src/GroupSplit.API/Services/PersonalGroupGuard.cs), which a new
group-scoped write path needs too. Cross-group views of balances leave it out.

### Participants

Everybody money is recorded against is a `User` row, whether or not they ever sign in.
`User.Kind` says which of three it is: an `Account`, a `StandIn` a group named, or an
account that was `Deleted` and anonymised.

A member is a `GroupMembership` row (the `GroupUser` table). Leaving, being removed and
deleting an account close the row with `LeftAt` rather than deleting it, and a model-wide
query filter keeps closed rows out of `Group.Users` and `User.Groups`, so "is this person a
member" means a current member without each read saying so. Archiving a group is per member,
on the same row.

A guest is a `GroupGuest`: a name the group chose, and a stand-in `User` that holds every
share recorded against them. A guest can be given a share, pay, be settled with and be
removed once square; an optional single-use link lets the real person claim the place, which
moves everything the stand-in held onto their account. An ended guest is filtered out the way
a closed membership is.

Somebody who has left, or a guest who was removed, is departed: their shares can no longer
move (`TRANSACTION_MOVES_DEPARTED_BALANCE`). Who may do what is in
[participants.md](participants.md), guests and their links in [guests.md](guests.md), and
what deleting an account keeps in [Deleting an account](keycloak.md#deleting-an-account).

### Transactions and splits

`Transaction` is an abstract base with two leaves, mapped as one table (TPH):

- `Expense`: something the group spent. It may be filed under a `Category`, and may have a
  `Receipt` and receipt attachments.
- `Transfer`: one person paying another back, with an optional `CoversThrough` day saying
  which period it settles. It has a single split, to the recipient, and can only be built
  through `Group.SettlementBetween` in
  [`TransferExtensions`](../src/GroupSplit.Data/Extensions/TransferExtensions.cs), so the
  split cannot be forgotten.

Both carry the payer (`UserId`), the group (always one), an amount to two decimal places, a
currency that must match the group's unless the group is personal, a UTC `DateTime`, a name,
and optional links to the bank row it was filed from, its merchant and the split-rule version
that divided it.

What each person owed is a `TransactionSplit` row, one per person per transaction, and the
splits of a transaction sum to its amount. They are worked out when the transaction is
written ([`ExpenseSplitter`](../src/GroupSplit.API/Services/ExpenseSplitter.cs), over
[`SplitCalculator`](../src/GroupSplit.Shared/Transactions/SplitCalculator.cs)) and stored, so a
balance is two sums over indexed columns: what somebody paid, minus what their splits say they
owed. `SplitCalculator` truncates every share to the cent and gives the remainder to the payer,
or, when the payer is not a participant, to the largest weight with ties broken by the smaller
id.

Settling up writes transfers (`SettlementService`), and a transfer is edited and deleted
through the same transaction routes as an expense. Anything that means expenses, such as a
listing or a total, reads `Set<Expense>()`, and EF's discriminator predicate keeps transfers
out of it.

### Categories, split rules and versions

A `Category` is what an expense was for. It may point at a `SplitRule` as its default, and
several categories can share one rule. The default pre-fills the division; it does not
enforce it, so one expense can be divided differently without touching the category. A
category with expenses under it cannot be deleted, so it is retired with `ArchivedAt`, which
takes it out of the pickers and leaves it on every expense filed under it.

A `SplitRule` is a name the group keeps. What it says is a `SplitRuleVersion`, and a version
is never edited: changing a rule closes the current version (`SupersededAt`) and opens a new
one. An expense records the version that divided it beside its splits, and the two answer
different questions. The splits are what each person owed and survive an edit to the rule;
the version is what produced them and survives an edit to the amount, which is what lets an
expense be divided again by the rule it had at the time.

Versions come in five kinds, a table each (TPT):

| Kind | Divides |
| --- | --- |
| `EvenSplitRuleVersion` | Evenly, between the people it names, or the whole current membership when it names nobody |
| `PercentSplitRuleVersion` | By percentage, stored as weights in hundredths of a percent (3333 is 33.33%) |
| `SharesSplitRuleVersion` | By whole shares |
| `SoleSplitRuleVersion` | All of it on one person |
| `ItemizedSplitRuleVersion` | By the items on the expense's bill, each item carrying its own division |

The first three derive from `WeightedSplitRuleVersion`, which owns the `SplitRuleParticipant`
weights. Every group is given a built-in Even rule as its default split, which an expense
under no category, or under one naming no rule, divides by; and a built-in "All for" rule
per participant, which puts the whole amount on that person.

What a kind does is its handler's, in `Services/SplitRuleHandlers`: an
`ISplitRuleHandler<TRule>` per kind, resolved by the version's runtime type through
`SplitRuleHandler`, and registered as singletons in
[`SplitRuleServiceExtensions.cs`](../src/GroupSplit.API/Extensions/SplitRuleServiceExtensions.cs).
Adding a kind is adding an entity, a DTO in `GroupSplit.Shared/SplitRules`, a handler and two
registrations, and nothing is edited in a switch. The `AppDbContext` registration is the one
that fails quietly if forgotten: an unregistered kind is stored and read back as its base and
dispatched to the wrong handler.

What a stored rule means once people join and leave is in
[split-rules-and-membership.md](split-rules-and-membership.md).

### Receipts

A `Receipt` is the itemised bill of one expense: subtotal, tax, tip and total, and its
`ReceiptItem` lines, each with a price, a quantity, its tax and the version that divides it.
A `ReceiptAttachment` is a photo or PDF in the RustFS `receipts` bucket, referenced by
object key; it can hang off a bank row before that row becomes an expense. Reading a file
into a bill goes to Veryfi or Azure OpenAI, picked by `ReceiptTranscription:Provider`, and is
off when neither is configured.

### Merchants

A `Merchant` is a place money is spent, with its logo. There is one row per normalised name,
shared across every group, because a merchant is not a group's opinion the way a category is.
Bank sync creates them as it meets new places, and expenses and bank rows point at them.

### Bank data

People link a bank, and its transactions arrive as `BankTransaction` rows under a
`BankConnection` and its `LinkedAccount`s. A bank row is staging data, and none of it is an
expense until somebody files it: filing copies the bank's figures into an ordinary `Expense`
and sets `Transaction.BankTransactionId`, one row to at most one expense. The provider sits
behind `IBankConnector`, with Plaid the only implementation. How sync, the inbox and the key
ring work is in [bank-sync.md](bank-sync.md), and how an imported row is matched against an
expense somebody already recorded is in [Duplicate detection](bank-sync.md#duplicate-detection).

## Errors

Every non-2xx response from the API is RFC 9457 problem details with a stable `code` from
`GroupSplit.Shared/Errors/ErrorCodes.cs`, a `traceId`, and extension members naming what is in
the way where that helps. Services throw domain exceptions and the handlers in
`GroupSplit.API/Errors` write the problem; the clients branch on `code` and choose their own
wording. The contract, the catalogue and how to add a code are in [errors.md](errors.md).

## Decisions that still hold

**Transactions are TPH, rule versions are TPT.** A transaction is one shape read two ways,
and its leaf columns are few and nullable by nature, so a table per leaf would buy a join and
nothing else. A rule version is five shapes that share a time window: under one table, the
one column `SoleSplitRuleVersion` needs would be nullable on all five, and nothing but a check
constraint would stop a weight hanging off a kind that has none. Since #260 each kind has its
own table, at the cost of five primary-key joins on every division read. `AppDbContext`
explains it beside `UseTptMappingStrategy`; read that before adding a sixth kind.

**The discriminator is indexed by hand.** EF does not index it, and `Set<Expense>()`
filters on nothing else. The index is declared after the leaves are, because until EF knows
the hierarchy there is no discriminator to index.

**What somebody owed is stored, not recomputed.** The splits are written with the
transaction and never derived on read. An edit to a rule or to the membership therefore
cannot restate what anybody owed last March, and balances need no arithmetic in SQL. Rule
history was dropped once on this reasoning and came back as `SplitRuleVersion` when the
question "divide this again by the rule it had at the time" got asked; it sits beside the
splits and does not replace them.

**The division arithmetic exists once.** It used to be written three times (in LINQ for the
balance query, in memory, and as a shares-to-percent conversion), and the three disagreed
about the last cent. `SplitCalculator` is the only copy. It lives in `GroupSplit.Shared`, so
the even starting point the expense editor fills its fields with is the same division, and
the seeder calls it too, because seed data divided differently from the app gives every
developer balances no sequence of user actions could produce. Percent and shares hand their weights to
it unchanged, with no conversion between them.

**Behaviour is dispatched by type, not by a `Kind` column.** A rule version is data, and what
it does belongs to a handler resolved from its runtime type. Without that, turning a wire DTO
into the right subtype would be a switch in the API layer, edited for every new kind.

**A category is a label that may name a rule.** The old model made the rule and the category
one thing, so an expense had to name a rule or could not be recorded, and two categories that
divided alike had to be two identical rules. A category now points at a rule, one rule can
serve many categories, and an expense under no category is ordinary.

**Currency lives on the group and on the transaction, and they must agree.** There is no
conversion, and a group that mixed currencies would produce balances that are wrong rather
than unavailable, so an expense in another currency is refused with `CURRENCY_MISMATCH`, and
so is moving one into a group that uses another. A personal expense keeps the currency it was
created or filed in, and the listing totals sum one currency at a time, reporting the others
beside the figure.

**Personal is a group (since #403).** For a while personal meant `GroupId` null, and personal
spending lost categories, a page and figures. Every account now has a group keyed on its own
id, `Transaction.GroupId` is required, and the `PersonalGroupIsBack` migration moved every
group-less expense into its payer's group.

**PATCH stays, with JSON Patch.** A patch can change part of a model, so for an expense's
shares "not mentioned" and "set to nothing" are different instructions. The model a patch is
applied to carries the stored shares, so silence about `/splits` keeps them (since
2026-09-09; reading silence as "divide again" re-divided 733 expenses). The exception is an
expense whose shares its rule produced: when the edit moves something they were worked out
from, such as the amount or the payer, it is divided again. `replace /splits null` asks for
a division outright, and a patch that moves the expense to another group without mentioning
`/splits` is divided afresh, since the old shares name the old group's people. Shares that no
longer sum to the amount are refused with `SPLITS_DO_NOT_SUM_TO_AMOUNT`, which names the
difference. `PatchDocumentExtensions.Touches` reads the operations before they are applied
to tell these cases apart; a `PUT` would delete it, and should when the verb changes. The
rest of the rule is in [split-rules-and-membership.md](split-rules-and-membership.md).

**The clients pin a preview of the JSON Patch package, on purpose.** The API takes the
released `Microsoft.AspNetCore.JsonPatch.SystemTextJson` 10.0.x. `GroupSplit.App.Shared` and
`GroupSplit.Cli` pin `10.0.0-preview.7.25380.108`, because every released 10.0.x declares a
framework reference on `Microsoft.AspNetCore.App`: the WebAssembly client cannot restore that
(NETSDK1082), and the CLI would need the ASP.NET Core runtime to name a `JsonPatchDocument`.
Do not unify the versions, write a patch type of our own, or bring in central package
management to hide the difference. When a released version ships as a plain library again, a
version bump is the whole change; check its nuspec first.

**A departure is guarded at removal, not on the rules.** Splits are historical facts, so a
departed member's past shares stay valid and no rule needs guarding for naming them. What
must not be lost is somebody who is owed money, so removing or leaving with a balance is
refused with `GROUP_MEMBER_NOT_SETTLED`.

**Members are equal, and a membership has no status column.** There are no roles, and a
closed row (`LeftAt`) is the only state a membership has besides open. Invitations name a
person (a guest) rather than an address.

**People are never deleted out from under the ledger.** References to a person are
`Restrict`: an account is anonymised, not deleted, because everybody else's balances are made
of its rows. Stand-ins are the only users ever deleted, and only after their shares have
moved, so a delete that would orphan a share fails.

**A migration never calls application code.** A data migration reproduces the formula it
needs rather than calling `SplitCalculator` or a service, because it must keep computing what
it computed on the day it ran, whatever happens to the production code afterwards.

**New columns are required, with a backfill.** Production data is small enough to give
existing rows a value outright (the migration that added `GroupMembership.JoinedAt` stamped
them with the day it ran), and a nullable column would make every reader carry a case that
only ever meant "old".
