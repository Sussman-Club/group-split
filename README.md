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
| `src/GroupSplit.ServiceDefaults` | Shared telemetry, health check, and service discovery defaults |
| `tests/GroupSplit.API.Test` / `tests/GroupSplit.AppHost.Test` | API and orchestration tests |

## Run locally

```bash
dotnet run --project src/GroupSplit.AppHost
```

## Email

Keycloak sends the password reset and email verification mail, so it needs an SMTP relay.

Locally there is nothing to configure. The AppHost runs [Mailpit](https://mailpit.axllent.org/),
every message Keycloak sends lands in its inbox, and nothing leaves the machine -- open the
inbox from the `mailpit` resource in the Aspire dashboard.

In production the relay comes from deployment values set on the `production` GitHub
environment. `SMTP_PASSWORD` is a secret; the rest are variables:

| Name | Example | Purpose |
| --- | --- | --- |
| `SMTP_HOST` | `smtp.resend.com` | Relay hostname |
| `SMTP_PORT` | `587` | Optional, defaults to 587 (submission over STARTTLS) |
| `SMTP_FROM` | `no-reply@example.com` | Sender address, on a domain the relay has verified |
| `SMTP_USER` | `resend` | SMTP username |
| `SMTP_PASSWORD` | | SMTP password or API key |

Leave them all unset and the deploy still succeeds: Keycloak simply cannot send mail, and
email verification stays off so registration keeps working. Setting only some of them
fails the deploy deliberately, because a realm that offers password reset over a relay
that rejects every send is worse than one that never offered it.

Whichever relay you use, expect to prove you own the sender domain by adding the SPF and
DKIM records it gives you. Mail from an unverified sender is rejected or filed as spam.

### Applying this to a realm that already exists

`realms.json` is the source of truth, but `start --import-realm` only ever creates a realm
that is not there yet -- it skips one the database already holds. A realm created before
these settings existed therefore will not pick them up. Either reset the Keycloak database
so the realm is imported afresh, or set the values once by hand under Realm settings ->
Email in the admin console, which also has a "Test connection" button worth using.

## Test

```bash
dotnet test tests/GroupSplit.API.Test
dotnet test tests/GroupSplit.AppHost.Test
```

## Tech stack

- .NET 10 (ASP.NET Core, Blazor, MAUI)
- PostgreSQL + Entity Framework Core
- .NET Aspire for local orchestration
