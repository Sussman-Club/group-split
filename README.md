# Group Split

Group Split is an expense-sharing app for groups of people.
It helps groups track shared expenses, apply split rules, and settle balances with fewer manual calculations.

## What it does

- Create and manage groups of members
- Record shared transactions
- Split costs using multiple rule types
- Calculate per-member balances and settlement needs
- Support web and mobile clients backed by a single API

## Project structure

| Component | Purpose |
| --- | --- |
| `src/GroupSplit.API` | ASP.NET Core API for groups, transactions, rules, and users |
| `src/GroupSplit.App/GroupSplit.App.Web` | Blazor web client |
| `src/GroupSplit.App/GroupSplit.App` | .NET MAUI client |
| `src/GroupSplit.Data` + `src/GroupSplit.Data.PostgreSQL*` | Domain models, data access, and PostgreSQL integration |
| `src/GroupSplit.AppHost` | .NET Aspire orchestration for local development |
| `src/GroupSplit.AppHost/keycloak-themes` | Custom Keycloak login theme (`group-split`) |
| `src/GroupSplit.ServiceDefaults` | Shared telemetry, health check, and service discovery defaults |
| `tests/GroupSplit.API.Test` / `tests/GroupSplit.AppHost.Test` | API and orchestration tests |

## Run locally

```bash
dotnet run --project src/GroupSplit.AppHost
```

## Test

```bash
dotnet test tests/GroupSplit.API.Test
dotnet test tests/GroupSplit.AppHost.Test
```

## Authentication

Keycloak is the identity provider. Sign-in, registration and password reset are all
served by Keycloak's own pages, themed to match the app -- the Blazor `/login` and
`/signup` routes are branded hand-offs and never collect credentials themselves. The
Blazor server acts as a BFF: it holds the session cookie and attaches access tokens
to API calls, so no token ever reaches the browser.

The realm is imported from `src/GroupSplit.AppHost/realms.json`, which enables
self-registration, password reset, a password policy and brute-force protection.

### Development sign-in

Seeded users are `daniel@test.com`, `loraine@test.com`, `anabel@test.com` and
`omar@test.com`, all with the password `groupsplit-dev`. (The realm now enforces a
minimum password length, so the previous `123` no longer satisfies the policy.)

Mail that Keycloak sends -- password resets, and email verification if you turn it on
-- is captured by MailPit rather than being delivered. Open the `mailpit` resource
from the Aspire dashboard to read it.

### Google sign-in

Disabled until credentials are configured. The realm imports the provider
disabled and hidden, so the login page is unchanged for anyone without them.

**The redirect URI is not stable.** Aspire assigns Keycloak's HTTPS port on
every start and offers no way to pin it
([aspire/#13807](https://github.com/microsoft/aspire/issues/13807)); the `port`
argument of `AddKeycloak` sets the HTTP port, which this setup does not use.
Google requires an exact redirect URI and does not accept wildcards, so the URI
has to be re-registered whenever the port changes.

1. Start the AppHost and read Keycloak's URL from the dashboard, or:

   ```bash
   docker port $(docker ps --format '{{.Names}}' | grep -E '^keycloak-[a-z]+$') 8443/tcp
   ```
2. In the [Google Cloud Console](https://console.cloud.google.com/apis/credentials),
   create an **OAuth client ID** of type **Web application** and add this
   **Authorised redirect URI**, substituting the port from step 1:

   ```
   https://localhost:<port>/realms/group-split/broker/google/endpoint
   ```
3. Store the credentials in user secrets, so they never reach git:

   ```bash
   dotnet user-secrets --project src/GroupSplit.AppHost set "Google:ClientId" "<client-id>"
   dotnet user-secrets --project src/GroupSplit.AppHost set "Google:ClientSecret" "<client-secret>"
   ```
4. Reset the realm (see below) and restart. "Google" now appears on the login page.

Keycloak substitutes the values into `realms.json` at import time via `${...}`
placeholders, so no credential is committed. `trustEmail` is on for this
provider, meaning a Google account's verified address is accepted without a
second confirmation email.

### Changing the realm

Keycloak stores its realm in Postgres, and the import runs with
`IGNORE_EXISTING`. Once the realm exists, edits to `realms.json` are **silently
skipped** -- the log still says "Import finished successfully", but without a
matching "Realm 'group-split' imported" line.

To apply a realm change, drop the Keycloak database and restart it:

```bash
DB=$(docker ps --format '{{.Names}}' | grep -E '^db-server-')
docker exec "$DB" psql -U postgres -c   "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='keycloak';"
docker exec "$DB" psql -U postgres -c "DROP DATABASE keycloak;"
docker exec "$DB" psql -U postgres -c "CREATE DATABASE keycloak;"
docker restart $(docker ps --format '{{.Names}}' | grep -E '^keycloak-[a-z]+$')
```

This discards anyone who self-registered in Keycloak; the four seeded users come
back from `realms.json`.

### Login theme

`keycloak-themes/group-split` is bind-mounted into the Keycloak container. It inherits
from `keycloak.v2` and layers brand CSS plus copy overrides, so no Keycloak template is
forked. Keycloak runs in dev mode with theme caching off, so edits show up on reload.

### Outside development

`WithRealmImport` is a development-only mechanism, and Aspire's service discovery
cannot satisfy `RequireHttpsMetadata`. Both the API and the web app therefore require
`Keycloak:Authority` to be configured when not running in Development, and will fail
fast at startup if it is missing.

## Tech stack

- .NET 10 (ASP.NET Core, Blazor, MAUI)
- PostgreSQL + Entity Framework Core (Keycloak is backed by Postgres too)
- Keycloak for authentication, MailPit for local mail capture
- .NET Aspire for local orchestration
