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
| `GroupSplit.API` | ASP.NET Core API for groups, transactions, rules, and users |
| `GroupSplit.App/GroupSplit.App.Web` | Blazor web client |
| `GroupSplit.App` | .NET MAUI client |
| `GroupSplit.Data` + `GroupSplit.Data.PostgreSQL*` | Domain models, data access, and PostgreSQL integration |
| `GroupSplit.Identity` | Authentication and identity data context |
| `GroupSplit.AppHost` | .NET Aspire orchestration for local development |
| `GroupSplit.API.Test` / `GroupSplit.AppHost.Test` | API and orchestration tests |

## Run locally

```bash
dotnet run --project GroupSplit.AppHost
```

## Test

```bash
dotnet test GroupSplit.API.Test
dotnet test GroupSplit.AppHost.Test
```

## Tech stack

- .NET 10 (ASP.NET Core, Blazor, MAUI)
- PostgreSQL + Entity Framework Core
- .NET Aspire for local orchestration
