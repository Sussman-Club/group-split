# Group Split

Group Split is an expense-sharing app for groups of people. It records what each person
paid, divides each expense by the group's split rules, and works out who owes whom. One API
holds the ledger, and a web app, a MAUI app and a command-line client sit in front of it.

## What it does

- Create a group, name whoever is sharing the costs, and send each of them a link to join by
- Record shared and personal expenses on one screen; every account has a personal group for
  what is nobody else's
- Split by a category's rule, by the group's default split (Even until somebody picks
  another), or by shares stated on one expense
- Split a bill by its items, so everybody pays for what they ordered, and read a photo or PDF
  of the receipt into a bill you can correct before it is saved
- Manage categories, split rules and each rule's versions from the app, and retire a category
  without losing what is filed under it
- Calculate per-member balances, and settle up from either side, or only up to a given day
- See where you stand across every group at once, and square up with one person across all
  of them in one payment
- Link a bank and file each row it sends to a group or to your personal group before any of
  it becomes an expense, and be told when a row is an expense somebody already recorded

## Architecture at a glance

The API owns the data and every rule about it; the web app, the MAUI app and the CLI are its
clients, and each signs in through Keycloak. The Aspire AppHost describes the whole stack
once. Run it and it starts everything on your machine, with the development tooling added;
publish it and it becomes the Docker Compose stack production runs.

- API (`src/GroupSplit.API`): ASP.NET Core over EF Core. Bank sync, receipt reading and the
  background jobs (`src/GroupSplit.Jobs`) run in its process.
- Web app (`src/GroupSplit.App/GroupSplit.App.Web` and `.Web.Client`): a Blazor Web host and
  its WebAssembly client. The host forwards to the API, so the browser sees one origin.
- MAUI app (`src/GroupSplit.App/GroupSplit.App`): Android, iOS, Mac Catalyst and Windows. Its
  pages are the web app's, from the shared Razor library `GroupSplit.App.Shared`.
- CLI (`src/GroupSplit.Cli`): `groupsplit`, built from the same OpenAPI document as the apps'
  API clients.
- PostgreSQL: one server for the app database and Keycloak's. The migrations
  (`src/GroupSplit.Data.PostgreSQL.Migrations`) run before the API starts.
- Keycloak: sign-in for every client. The `realm-settings` worker
  (`src/GroupSplit.RealmWorker`) reconciles the realm with `realms.json` on every start.
- Redis for the web app's sign-in sessions, and RustFS for receipt files.
- Aspire AppHost (`src/GroupSplit.AppHost`): the app model for all of the above.

[docs/architecture.md](docs/architecture.md) has every project, the request path, the domain
model and the design decisions that still hold.

## Quick start

You need the .NET 10 SDK (`global.json` pins 10.0.100 and rolls forward), Docker running, the
[Aspire CLI](https://aspire.dev) 13.6 to match the AppHost SDK, and a trusted ASP.NET Core
development certificate (`aspire certs trust`).

```bash
aspire start
```

`aspire start` prints the dashboard's address. Open it, wait for `web` to be running, and
open the web app from its endpoint. `dotnet run --project src/GroupSplit.AppHost` does the
same from an IDE.

The seeder does not start on its own. Start the `seeder` resource from the dashboard, or run
its **Reset databases and seed** command to empty the app database first. It fills the app
database and the Keycloak realm from
[`users.json`](src/GroupSplit.Seeder/SeedData/users.json), and you can then sign in as any of
these:

| Email | Password |
| --- | --- |
| `daniel@test.com` | `GroupSplit123!` |
| `anabel@test.com` | `GroupSplit123!` |
| `loraine@test.com` | `GroupSplit123!` |
| `omar@test.com` | `GroupSplit123!` |
| `sofia@test.com` | `GroupSplit123!` |
| `mateo@test.com` | `GroupSplit123!` |
| `priya@test.com` | `GroupSplit123!` |

Bank sync and receipt reading stay off until their credentials are in the AppHost's user
secrets. [docs/getting-started.md](docs/getting-started.md) covers those, the dashboard,
Mailpit, pointing the CLI at a local stack, and building with no SDK installed.

## Tests

```bash
dotnet test --project tests/GroupSplit.API.Test
```

`global.json` makes Microsoft Testing Platform the runner, so `dotnet test` takes a project
with `--project`. CI runs five suites like this one on every pull request to `dev` or `main`,
fails the build when merged coverage drops below its floor, and runs
`GroupSplit.AppHost.Test` against the whole stack in Docker. [docs/testing.md](docs/testing.md)
has the rest.

## Documentation

Start here:

- [getting-started.md](docs/getting-started.md): prerequisites, running the stack, demo
  accounts, user secrets, Mailpit, the CLI against a local stack
- [architecture.md](docs/architecture.md): the projects, run and publish mode, the request
  path, data and migrations, background jobs, the domain model
- [testing.md](docs/testing.md): the suites, how to run them, the coverage gate, the
  integration tests

How it works:

- [split-rules-and-membership.md](docs/split-rules-and-membership.md): rule versions, what a
  stored rule means when people join and leave, retiring a category
- [guests.md](docs/guests.md): people a group shares costs with who have no account, and the
  link that lets one claim their place
- [participants.md](docs/participants.md): members, guests and leavers, and what each may do
- [bank-sync.md](docs/bank-sync.md): the connector seam, the inbox, catching a bank row that
  is an expense somebody already recorded, webhooks
- [keycloak.md](docs/keycloak.md): sign-in, how a sign-in is linked to an account, deleting
  an account, the realm and its worker, the clients, Google, email

Reference:

- [cli.md](docs/cli.md): every `groupsplit` command, installing it, the output and exit-code
  contract
- [errors.md](docs/errors.md): the problem-details contract and the error code catalogue
- [design-system.md](docs/design-system.md): the shared palette and type, and every file
  that carries a copy
- [skills/README.md](skills/README.md): the agent skills that drive the CLI

Operating it:

- [deployment.md](docs/deployment.md): how a deploy runs, its parameters, the key rings,
  sessions, and reaching the deployed dashboard

## Tech stack

- .NET 10: ASP.NET Core, Blazor (Web and WebAssembly) with MudBlazor, .NET MAUI
- PostgreSQL with Entity Framework Core
- Keycloak for sign-in
- Aspire 13.6 for local orchestration and for publishing to Docker Compose
- Redis, RustFS
- Plaid for bank data; Veryfi or Azure OpenAI for reading receipts
- System.CommandLine and Spectre.Console for the CLI
- xUnit v3, bUnit, Moq and Playwright for tests
