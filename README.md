# Group Split

Group Split is an expense-sharing app for groups of people.

It helps you:
- track shared expenses in a group
- split costs using different rules
- calculate balances so members can settle up

## Project structure

- `GroupSplit.API`: ASP.NET Core API for groups, transactions, rules, and users
- `GroupSplit.App/GroupSplit.App.Web`: Blazor web client
- `GroupSplit.App`: .NET MAUI app
- `GroupSplit.Data*`: data access and PostgreSQL integration
- `GroupSplit.AppHost`: .NET Aspire orchestration for local development

## Run locally

```bash
dotnet run --project GroupSplit.AppHost
```

## Test

```bash
dotnet test GroupSplit.API.Test
dotnet test GroupSplit.AppHost.Test
```
