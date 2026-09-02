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
