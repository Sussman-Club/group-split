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

## Test

```bash
dotnet test tests/GroupSplit.API.Test
dotnet test tests/GroupSplit.AppHost.Test
```

## Tech stack

- .NET 10 (ASP.NET Core, Blazor, MAUI)
- PostgreSQL + Entity Framework Core
- .NET Aspire for local orchestration
