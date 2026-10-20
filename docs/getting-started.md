# Getting started

Group Split runs locally from one Aspire AppHost, `src/GroupSplit.AppHost`: Postgres,
Keycloak, Redis, the receipt store, the API and the web app, plus the development tools a
deployment never gets. This page goes from a fresh clone to signing in as a demo account.

## Prerequisites

- The .NET SDK that `global.json` names: `10.0.100`, with `rollForward: latestFeature`, so any
  10.0 SDK from the 1xx feature band up will do.
- Docker, with the daemon running. Every backing service is a container.
- The Aspire CLI, on the same version as the AppHost SDK: the `Aspire.AppHost.Sdk/<version>`
  at the top of `src/GroupSplit.AppHost/GroupSplit.AppHost.csproj`, 13.6.0 at the time of
  writing. CI installs it as a global tool, and so can you:

  ```bash
  dotnet tool install -g Aspire.Cli --version 13.6.0
  ```

  `bash .github/scripts/get-aspire-apphost-sdk-version.sh` prints the version the AppHost
  pins, which is where CI reads it from.
- A trusted ASP.NET Core development certificate. Keycloak sends a sign-in back only to the
  web app's HTTPS endpoint, and the browser has to accept it:

  ```bash
  dotnet dev-certs https --trust
  ```

- The repository's local tools, `dotnet-ef` and `nswag`, pinned in `dotnet-tools.json` at the
  root. Running the app does not need them; adding a migration needs `dotnet-ef`:

  ```bash
  dotnet tool restore
  ```

The MAUI client needs the MAUI workload (`dotnet workload install maui`), and only if you are
going to build it. Nothing else depends on it: the AppHost reaches the MAUI project by path
rather than by project reference, CI never builds it, and the dashboard builds it only when
somebody starts it.

For Android configuration and the native-app acceptance checklist, see [Android](android.md).

## Run it

From the root of the checkout:

```bash
aspire start
```

`aspire.config.json` points the CLI at the AppHost.
`dotnet run --project src/GroupSplit.AppHost` does the same from an IDE and opens the
dashboard in a browser. Either way the AppHost is in run mode, the only mode that adds the
seeder, Mailpit, Scalar, the CLI and MAUI resources and the realm-reset command.
[docs/deployment.md](deployment.md) covers what publish mode does instead.

The dashboard lists these resources:

| Resource | What it is for |
| --- | --- |
| `db-server` | Postgres, on a data volume. DBX and a `psql` shell in the dashboard's terminal dock |
| `db`, `keycloak-db` | The app's database (`groupsplit`) and Keycloak's (`keycloak`), each with a Postgres MCP server an agent can query through |
| `migrations` | Applies the EF Core migrations to `db`, then exits. The API waits for it |
| `cache` | Redis, on a data volume. Holds the web app's sign-in tickets and tokens. DBX and `redis-cli` |
| `storage`, `receipts` | RustFS, an S3-compatible store for receipt files, and the bucket the orchestrator creates in it |
| `keycloak` | The identity provider. Imports the `group-split` realm from `Assets/keycloak/realms.json` on first start, mounts the theme from the checkout, sends mail to Mailpit, and keeps its data in a volume |
| `realm-settings` | A one-shot worker that brings the realm's managed settings into line with `realms.json` and exits. The API waits for it. Carries the "Reset local Keycloak realm" command, which runs `realm-reset` |
| `mailpit` | Catches every mail Keycloak sends |
| `api` | The ASP.NET Core API |
| `web` | The Blazor web app, where you sign in |
| Scalar | The API reference, wired to `api` |
| `seeder` | Fills the app database and the realm with demo data. Explicit start; see below |
| `cli` | `groupsplit auth status` against this run's API and realm. Explicit start |
| `app` | The MAUI client on a Windows device, built when started. Explicit start |
| `veryfi`, `plaid`, `azure-openai` | External services, so their calls appear as named dependencies in traces. Nothing of theirs runs locally |
| `webhook-tunnel` | Present only when `WebhookTunnel` is on; see [docs/bank-sync.md](bank-sync.md) |

The web app is the `web` resource's `https` URL. Use that one: Keycloak refuses a sign-in
that starts from any other origin, the web app's plain HTTP endpoint included.

The first start is the slow one: it pulls every image and initialises the volumes. The
AppHost also generates the Postgres, Redis, RustFS and Keycloak admin passwords then, stores
them in its user secrets, and the volumes are initialised with them. Clearing those user
secrets leaves a fresh password facing an old volume, and the containers refuse it.

## Demo accounts and the seeder

The seeder does not start on its own. Start the `seeder` resource from the dashboard, or run
its "Reset databases and seed" command to start from scratch, which drops and re-migrates the
app database first. It fills the app database with groups, guests, categories, merchants,
expenses, settlements and bank connections from `src/GroupSplit.Seeder/SeedData/`, and creates
the Keycloak accounts from
[`users.json`](../src/GroupSplit.Seeder/SeedData/users.json). Every account has the same
password:

| Email | Password |
| --- | --- |
| `daniel@test.com` | `GroupSplit123!` |
| `anabel@test.com` | `GroupSplit123!` |
| `loraine@test.com` | `GroupSplit123!` |
| `omar@test.com` | `GroupSplit123!` |
| `sofia@test.com` | `GroupSplit123!` |
| `mateo@test.com` | `GroupSplit123!` |
| `priya@test.com` | `GroupSplit123!` |

The password is `Keycloak:DefaultPassword` in the seeder's `appsettings.json`; a seed entry
may name its own instead.

The database row and the Keycloak account come from the same entry and share one id. The
seeder creates each Keycloak account with the entry's `ExternalUserId`, which is the identity
id the database row is linked by, so signing in lands on the seeded groups and expenses. The
API links an account by the token's subject and provisions a new one for a subject it has not
seen. An account registered by hand
carries a subject the seed data has never heard of, so the API tries to create a second
account with the same address, and every request fails on the unique email index. The seeder
replaces such an account and says so in its log; set `Keycloak:ReplaceConflictingUsers` to
`false` in the same `appsettings.json` to be warned and left alone instead.

Keycloak keeps its users in a data volume, so the accounts outlive a restart, and a second run
of the seeder leaves them alone. "Reset databases and seed" resets the app database and not
Keycloak's, which is fine: the ids are fixed in `users.json`, so the two ends still agree.
"Reset local Keycloak realm" on `realm-settings` is the other way round. It deletes the realm
and its users and lets Keycloak import `realms.json` again, after which the seeder has to run
once more to put the accounts back.

The accounts exist only in local run mode. A deployment has no seeder, and `realms.json` holds
no users, because it ships to deployments.

## Secrets you may want

With nothing set, everything runs. Three things are off: linking a bank (the app says bank
sync is off), reading a receipt into a bill (the API answers
`RECEIPT_TRANSCRIPTION_UNAVAILABLE`; attaching the file still works), and Google on the login
page.

Each is switched on by Aspire parameters, which run mode reads from configuration under
`Parameters:<name>`. Put them in the AppHost's user secrets and restart the AppHost:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:<name> <value>
```

`dotnet user-secrets list --project src/GroupSplit.AppHost` shows what is there, the
generated passwords included. The Keycloak admin console is on the `keycloak` resource's URL;
the user is `admin` and the password is `Parameters:keycloak-password`.

### Bank sync

Plaid's sandbox needs no approval. Take the client id and the sandbox secret from the Plaid
dashboard:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-secret <sandbox secret>
```

That is all a local run needs. `plaid-env` defaults to `Sandbox`; the API links banks whenever
it has both credentials, so `plaid-enabled` only matters to a publish; and the key-ring
certificate is required outside Development only. In Plaid Link, any sandbox institution
opens with `user_good` / `pass_good`.

Plaid's webhooks need an address it can reach from outside, which a laptop has only behind a
dev tunnel; [docs/bank-sync.md](bank-sync.md) has the setup. Without one, a sync runs when the
app asks for one, on linking or through "Sync now", and the nightly sweep catches the rest.

### Receipt transcription

Two providers can read a receipt. Veryfi takes four values:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:veryfi-enabled true
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:veryfi-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:veryfi-username <username>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:veryfi-api-key <key>
```

Azure OpenAI takes four, and the endpoint is the v1 base URL:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:azure-openai-enabled true
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:azure-openai-endpoint https://<resource>.openai.azure.com/openai/v1/
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:azure-openai-model <deployment name>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:azure-openai-api-key <key>
```

`receipt-transcription-provider` picks one, `Veryfi` or `AzureOpenAI`. Left empty, the API
uses Veryfi if all four of its values are set, and Azure OpenAI if it is enabled.

### Google sign-in

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-sign-in-enabled true
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-client-secret <secret>
```

The OAuth client in the Google Cloud console needs Keycloak's broker endpoint,
`<keycloak URL>/realms/group-split/broker/google/endpoint`, as an authorised redirect URI.
Keycloak's port is assigned per run, so the registered URI has to follow it.

Keycloak reads these three when it imports the realm, and the local realm was imported on the
first start. Setting them afterwards changes nothing until the realm is imported again: run
"Reset local Keycloak realm" on `realm-settings`, then start the seeder.

## Mail

Keycloak sends the password reset and email verification mail. Locally it all goes to
[Mailpit](https://mailpit.axllent.org/) and nothing leaves the machine; there is nothing to
configure. Open the inbox from the `mailpit` resource's URL in the dashboard. The app itself
sends no mail.

## The CLI against your local stack

The `cli` resource is enough to check the wiring: it runs `groupsplit auth status` with this
run's API and realm URLs and exits. To work with the CLI, run it from the checkout with
`GROUPSPLIT_API_URL` and `GROUPSPLIT_AUTHORITY` pointing at the stack, then sign in as a demo
account through the device flow:

- [Running it against the local stack](cli.md#running-it-against-the-local-stack) reads both
  URLs back from `aspire describe`, since the ports change per run.
- [Without installing](cli.md#without-installing) and
  [From the checkout instead](cli.md#from-the-checkout-instead) cover `dotnet run` and a
  locally packed tool.
- [Signing in](cli.md#signing-in) covers `groupsplit auth login`.

## Building in a sandbox with no SDK

CI installs .NET with `actions/setup-dotnet`, which fetches it from
`builds.dotnet.microsoft.com`. Some sandboxes, Claude Code's remote environment among them,
allow only a narrow set of hosts, and that is not one of them: the download fails with a 403
from the egress proxy, and so do `dot.net`, `dotnetcli.azureedge.net` and `aka.ms`. Building
there still works, because Ubuntu ships a .NET 10 SDK that satisfies `global.json`, and
`packages.microsoft.com` and `api.nuget.org` are usually reachable:

```bash
apt-get install -y dotnet-sdk-10.0     # 10.0.1xx, which global.json's rollForward accepts
dotnet tool restore                    # dotnet-ef and nswag, from api.nuget.org
```

Docker is the likelier casualty: the client is usually present with no daemon behind it, so
the AppHost, the integration tests and anything using Testcontainers cannot run. The five unit
test projects CI runs need none of it; [docs/testing.md](testing.md) lists them. For work that
does need a database, such as running a migration against real PostgreSQL rather than
reasoning about its SQL, a server from the distribution is enough and needs no daemon:

```bash
apt-get install -y postgresql
su postgres -c "/usr/lib/postgresql/16/bin/pg_ctl -D /var/lib/postgresql/16/main \
    -o '-c config_file=/etc/postgresql/16/main/postgresql.conf' -l /tmp/pg.log start"
su postgres -c "psql -c \"CREATE ROLE my_user LOGIN PASSWORD 'my_password' SUPERUSER;\""
su postgres -c "psql -c 'CREATE DATABASE my_db OWNER my_user;'"
```

Those names are the connection string `AppContextPostgreSqlFactory` falls back to when none is
configured, so `dotnet ef database update --project src/GroupSplit.Data.PostgreSQL.Migrations`
finds it with no further setup.

## Where to go next

- [docs/architecture.md](architecture.md): how the projects fit together.
- [docs/testing.md](testing.md): the test suites, and what the integration tests need.
- [docs/keycloak.md](keycloak.md): the realm, the `realm-settings` worker and its reset.
- [docs/bank-sync.md](bank-sync.md): bank sync, and receiving Plaid's webhooks locally.
- [docs/deployment.md](deployment.md): publish mode, the deployment parameters and the deploy.
