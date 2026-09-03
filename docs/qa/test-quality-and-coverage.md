# QA review: test quality, code coverage and infrastructure coverage

Reviewed at `b1c686c` (tip of `dev`, 2026-09-03). Numbers in this document were
measured, not estimated: reproduce them with

```bash
dotnet test --project tests/GroupSplit.API.Test/GroupSplit.API.Test.csproj -c Release --results-directory TestResults -- --coverage --coverage-settings tests/coverage.config --coverage-output-format cobertura --coverage-output api.cobertura.xml
```

and the same for `GroupSplit.App.Web.Test`, then merge with ReportGenerator as the
CI workflow does.

## Summary

The suite is small but the good parts are genuinely good. The authentication and
user-provisioning tests read like tests written by someone who had just been burned
by the bug they pin, and they assert on behaviour rather than on implementation. The
older service tests are weaker, and the gap in what is tested at all is the real
finding: **every HTTP endpoint, the money-splitting algorithm, and all 1,477 lines of
hosting, deployment and seeding code have no automated coverage whatsoever.**

Nothing here is failing. Every test that runs, passes. The risk is entirely in what
does not run.

| | |
|---|---|
| Test methods | 75 (66 API, 7 web sign-in, 2 Aspire integration) |
| Test cases executed | 74 — the one `[Theory]` contributes two |
| Executed in CI before this branch | 74; the 2 integration tests were built and skipped |
| Line coverage (hand-written code) | **45.1%** |
| Branch coverage | **32.6%** |
| Infrastructure code under test | ~0% |

## What was changed on this branch

1. **Coverage is now measured.** `Microsoft.Testing.Extensions.CodeCoverage` is
   referenced by all three test projects, with shared filters in
   `tests/coverage.config`.
2. **CI reports and gates on it.** The unit-test job merges the per-project
   Cobertura files, writes the summary onto the run page, and fails if line or
   branch coverage falls below a recorded floor (`tests/coverage-gate.py`).
3. **The integration tests run.** `GroupSplit.AppHost.Test` was built by CI and then
   never executed. It now has its own job with Docker and Playwright. It is
   `continue-on-error` for now — see [Open question](#open-question-the-integration-job).

The filters in `tests/coverage.config` matter for reading any of these numbers. An
unfiltered run reports **32.0%**, but that denominator includes ~1,800 lines of
source-generated OpenAPI and validation code in the API assembly alone — four times
the hand-written code beside it, permanently at 0%, and free to move the headline
figure whenever a generator changes. Filtered, the 45.1% is a number about code
someone actually wrote.

## Coverage by area

### API — 43.9% line, 35.4% branch

| File | Line | Note |
|---|---|---|
| `Endpoints/GroupApi.cs` | **0%** | 436 lines |
| `Endpoints/TransactionApi.cs` | **0%** | 240 lines |
| `Endpoints/RulesApi.cs` | **0%** | 197 lines |
| `Endpoints/UserApi.cs` | **0%** | |
| `Services/DebtCalculationService.cs` | **0%** | the debt-minimisation algorithm |
| `Services/RuleVersionHandlers/SharesRuleVersionHandler.cs` | **0%** | |
| `Middleware/CurrentUserMiddleware.cs` | **0%** | |
| `Services/TransactionService.cs` | 61.2% | |
| `Services/UserProvisioner.cs` | 88.7% | |
| `Services/GroupService.cs` | 90.6% | |
| `Services/RulesService.cs` | 98.1% | |

### Web app — 33.7% line, 17.3% branch

| File | Line | Note |
|---|---|---|
| `Services/TokenRefreshService.cs` | **0%** | 194 lines, silent-refresh path |
| `Services/AuthDelegatingHandler.cs` | **0%** | |
| `RenderModeConfig.cs`, `WebAppExtensions.cs` | **0%** | |
| `Services/DistributedCacheTicketStore.cs` | 6.2% | |
| `IdentityApi.cs` | 60.6% | |
| `AuthenticationExtensions.cs` | 96.9% | |

`GroupSplit.App.Shared` and `GroupSplit.App.Web.Client` do not appear at all: no test
loads them, and there are no Blazor component tests of any kind.

### Data — 97.9%

Almost entirely the `AppDbContext` model configuration, exercised as a side effect of
every service test. Real, but it does not mean the model is verified — see
[In-memory](#the-in-memory-provider-is-not-the-database-we-ship).

### Shared — 5.6%

The two custom validation attributes are untested and both have edge cases worth
pinning:

- `GreaterThanAttribute.IsValid` returns a failure for anything that is not a
  `decimal`, **including `null`**. On an optional property it therefore rejects the
  request when the field is simply absent, which is unlikely to be intended.
- `MaxDecimalPlacesAttribute` computes its scale as `(decimal)Math.Pow(10, n)` —
  a `double` round-trip — and then multiplies. Both the precision loss and the
  overflow on large values are untested.

### Infrastructure — no automated coverage

1,477 lines across `GroupSplit.AppHost`, `GroupSplit.ServiceDefaults` and
`GroupSplit.Seeder`. `GroupSplit.AppHost.Test` was the only thing that touched any of
it, and CI built it without running it, so in practice a deploy was the first place a
break in this code could show up.

Highest-value target: `SeederOrderingExtensions.TopologicallySort`. It is 82 lines of
Kahn's algorithm with explicit cycle detection and an "unregistered dependency" error
path, it is pure, it needs no host, and it has no tests. Two dozen lines of test would
cover all of it.

## Test quality findings

Ordered by how much they cost.

### 1. A test that cannot fail

`tests/GroupSplit.API.Test/Group/GroupMemberAddTests.cs:33`

```csharp
var exception = await Record.ExceptionAsync(async () => { await groupService.AddGroupMembers(...); });
Assert.Null(exception);
```

`AddGroupMembers_AddsMembersToGroup` never checks that a member was added. An
implementation whose body was deleted would pass it. The sibling test three methods
down (`AddGroupMembers_EmailsNotFound_NoUsersAdded`) already shows the fix: call
`GetGroupMembers` and assert on the result.

### 2. An integration test with no assertions

`tests/GroupSplit.AppHost.Test/Data/DataTest.cs`

`TestDateTimeWithOffset` builds a `Transaction` with a `-08:00` offset, saves it, and
stops. It fails only if `SaveChangesAsync` throws. The interesting question — whether
the offset survives the Postgres round-trip — is exactly what is not asked. Reading
the row back and asserting on `DateTime` would turn it into the test its name claims.

The comment says the transaction "will roll-back the changes"; it does, but only via
`DisposeAsync`, which is worth an explicit `RollbackAsync` or a note, because the
current form silently depends on nothing later committing.

### 3. The in-memory provider is not the database we ship

Every API test runs on `Microsoft.EntityFrameworkCore.InMemory` while production runs
Postgres. That provider does not enforce foreign keys, unique indexes or required
columns, does not translate SQL, and has no transactions. So `GroupService` at 90.6%
line coverage tells you the C# branches ran, not that the queries work — anything that
fails only in translation, or only under a real constraint, passes here.

This is a reasonable trade for fast unit tests. It is not reasonable as the *only*
database the suite sees, which is what it was while the AppHost tests never ran.
Getting that job green is the mitigation, not rewriting these tests.

### 4. No test reaches an HTTP endpoint

All 66 API tests resolve a service from a container and call it directly. Nothing
exercises route mapping, model binding, the authorization policies, the status codes
the endpoints map exceptions onto, or `CurrentUserMiddleware`. Those 900 lines of
endpoint code are the app's actual contract with its clients.

A `WebApplicationFactory`-style host — the web project's `SignInHost` is a good local
model for how to boot only what you need — would cover it.

### 5. Assertions on counts that include invisible rows

`tests/GroupSplit.API.Test/Rules/RulesListTests.cs:49`

```csharp
Assert.Equal(3, list.Count); // Including the personal rule version from the user's personal group
```

The comment is doing the work the assertion should. Asserting on the identity of the
expected versions, as the test below it does, survives someone adding a second
implicit rule.

### 6. Uneven conventions

Minor, but they add friction:

- `GroupMemberAddTests.cs` declares `class GroupMemberAddTest`; `GroupMemberDelete.cs`
  has no suffix at all. Three naming schemes across one directory.
- Two naming styles for test methods: `Settle_CreatesTwoTransactions` in the older
  files, `A_profile_edited_in_keycloak_reaches_the_stored_user` in the newer ones. The
  second reads far better in a failure report. Worth picking one.
- One `[Theory]` in the entire suite. Several files repeat near-identical `[Fact]`s
  that differ only in an input value.
- `tests/GroupSplit.AppHost.Test/Configuration/` is empty.

### 7. Fixed: the integration project could not have run in CI

`GroupSplit.AppHost.Test` did not reference `Microsoft.Testing.Extensions.TrxReport`,
which the other two projects carry with a comment explaining that MTP exits with code
5 without it. Had anyone enabled the job with the same `--report-trx` flags, it would
have failed before running a test. Added on this branch.

## Recommended next steps

In value order. The first three are small.

1. **Fix the two tests that cannot fail** (findings 1 and 2). Under an hour.
2. **Test `SeederOrderingExtensions.TopologicallySort`** — layering, the cycle error,
   the unregistered-dependency error. Pure function, no host, ~25 lines of test.
3. **Test the two validation attributes**, including `null` and non-decimal input.
   Expect finding 4's `null` case to force a decision about intended behaviour.
4. **Test `DebtCalculationService`.** It is the product. Worth covering: a settled
   group, one debtor against many creditors, fractional amounts, and the
   `ArgumentException` when the current user is absent from the balances.
5. **Add endpoint tests** for at least the authorization boundaries — a member of no
   group asking for another group's transactions should be a test, not a hope.
6. **Get the integration job reliably green**, then delete its `continue-on-error`.
7. **Raise the coverage floor** in `.github/workflows/ci.yml` as each of the above
   lands. The floor is a ratchet; it is only useful if someone turns it.

## Open question: the integration job

The Aspire integration tests were run locally against Docker during this review, on a
machine with Playwright's browsers already installed. After 13 minutes they had
produced no output and were stopped. The containers came up within seconds
(`db-server`, `keycloak`, `keycloak-db`, `pgweb`, `scalar`, the network tunnel proxy)
and a `GroupSplit.API` process started a minute later, but **no web-app process and no
seeder process ever appeared** — which is precisely what both tests block on:
`WebPageTest` awaits `WaitForResourceHealthyAsync("web")`, and `DataTest` awaits the
seeder reaching `Finished`.

So this is not slow-but-progressing; two project resources are not starting. Worth
someone reproducing before the job is made blocking.

That is why the CI job carries `continue-on-error`. Two things would help regardless
of the cause:

- **Give `GroupSplit.AppHost.Test` an explicit timeout.** A hang currently consumes a
  runner for the whole job timeout and reports nothing useful. xUnit v3 takes a
  per-test `Timeout`, and the `WaitFor…` calls should take a bounded
  `CancellationToken` rather than `TestContext.Current.CancellationToken` alone.
- **Capture the AppHost resource logs on failure** so the CI artifact says which
  resource never started, instead of just showing a cancelled run.
