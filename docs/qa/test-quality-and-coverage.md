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
| Test methods | 76 (66 API, 7 web sign-in, 3 Aspire integration) |
| Test cases executed | 77 — the one `[Theory]` contributes two |
| Executed in CI before this branch | 74; the integration tests were built and skipped |
| Line coverage (hand-written code) | **45.1%** |
| Branch coverage | **32.6%** |
| Infrastructure code under test | ~0% |

> **Superseded in part.** A later branch acted on steps 1 to 4 of
> [Recommended next steps](#recommended-next-steps); the API test count is now 153 and
> coverage is **57.84% line, 48.87% branch**, with the CI floors at 54/45. The figures
> in the rest of this document are the ones measured at `b1c686c` and are kept as the
> before picture — see [What the follow-up branch covered](#what-the-follow-up-branch-covered).

## What was changed on this branch

1. **Coverage is now measured.** `Microsoft.Testing.Extensions.CodeCoverage` is
   referenced by all three test projects, with shared filters in
   `tests/coverage.config`.
2. **CI reports and gates on it.** The unit-test job merges the per-project
   Cobertura files, writes the summary onto the run page, and fails if line or
   branch coverage falls below a recorded floor (`tests/coverage-gate.py`).
3. **The integration tests run in CI.** `GroupSplit.AppHost.Test` was built by CI
   and never executed. Turning it on surfaced a real bug in the app, described in
   [The reason they never finished](#the-reason-they-never-finished); with that fixed
   the whole suite runs in about 40 seconds against warm images. The job it needs was
   pulled once over a certificate failure on the runner and is back — see
   [Why the job was out, and what brought it back](#why-the-job-was-out-and-what-brought-it-back).
4. **Nothing waits forever.** The fixture bounds start-up and every `WaitFor…`, each
   test carries a `Timeout`, and the CI job has `timeout-minutes`. A stall now fails
   with the last state of every resource instead of hanging.

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
break in this code could show up. That is no longer hypothetical: turning those tests
on immediately found a health-probe bug in `ServiceDefaults` that had been breaking
every local run — see [The reason they never finished](#the-reason-they-never-finished).

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

### 2. Fixed: an integration test with no assertions

`tests/GroupSplit.AppHost.Test/Data/DataTest.cs`

`TestDateTimeWithOffset` built a `Transaction` with a `-08:00` offset, saved it, and
stopped. It failed only if `SaveChangesAsync` threw. The interesting question —
whether the offset survives the Postgres round-trip — was exactly what it did not ask.
It now reads the value back through a fresh `AsNoTracking` query, rather than off the
tracked entity, which would hand back what was already in memory without touching the
column at all.

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

1. ~~**Fix the test that cannot fail** (finding 1).~~ Done.
2. **Test `SeederOrderingExtensions.TopologicallySort`** — layering, the cycle error,
   the unregistered-dependency error. Pure function, no host, ~25 lines of test.
   Still open, and it needs a home: `GroupSplit.Seeder` is reachable from
   `GroupSplit.AppHost.Test`, whose coverage goes to the integration job, not to the
   gate the floors apply to.
3. ~~**Test the two validation attributes**, including `null` and non-decimal input.~~
   Done, and the `null` case is pinned rather than fixed — see below.
4. ~~**Test `DebtCalculationService`.**~~ Done.
5. **Add endpoint tests** for at least the authorization boundaries — a member of no
   group asking for another group's transactions should be a test, not a hope. Still
   open, and now the single largest gap: `GroupApi`, `TransactionApi` and `RulesApi`
   are 372 uncovered lines between them.
6. ~~**Raise the coverage floor** in `.github/workflows/ci.yml` as each of the above
   lands.~~ Done: 41/32 to 54/45.

## What the follow-up branch covered

Measured the same way as the figures above, before and after:

| | Line | Branch |
|---|---|---|
| Before | 45.92% | 33.40% |
| After | **57.84%** | **48.87%** |

The API test count went from 70 executed cases to 153. What moved:

| Area | Before | After |
|---|---|---|
| `Services/DebtCalculationService.cs` | 0% | covered |
| `Services/RuleVersionHandlers/SharesRuleVersionHandler.cs` | 0% | 98.5% |
| `Services/RuleVersionHandlers/PercentRuleVersionHandler.cs` | 92.2% | covered incl. the `Equals` paths |
| `Middleware/CurrentUserMiddleware.cs` | 0% | covered |
| `Shared/CustomValidationAttributes` | 0% | covered |
| `Services/TransactionService.cs` | 61.2% | the details, edit-model and update paths |

Two things worth knowing about what these tests assert:

- **The validation attributes are pinned, not fixed.** `GreaterThanAttribute` still
  rejects `null` and still rejects any non-`decimal`, and `MaxDecimalPlacesAttribute`
  still throws `OverflowException` rather than returning false on a value too large to
  scale. The tests say so in as many words, so changing the behaviour breaks a test
  that describes what it was rather than one that claims it was right.
- **One behaviour was changed, not just covered.** See
  [A group with no rule](#a-group-with-no-rule).

## What a feature-by-feature review found

Reading every service against the tests turned up three defects. All three are fixed on
this branch, each with a test that fails without the fix.

### Two reads ignored the authorization model

Authorization in this API is entirely the query scoping in the services: every read
starts from `dbContext.Entry(currentUser).Collection(u => u.Groups)`, and the endpoints
add nothing of their own — they take an id from the route and pass it straight down.

Two reads did not start there. `TransactionService.GetDetails` and
`RulesService.GetRuleDetails` queried `dbContext.Set<...>()` over the whole table, so any
signed-in caller who knew an id got back:

- the transaction's amount, name, category, group name, who paid, and the full per-member
  split with names; and
- the rule's category and the split itself — every member's percentage or share count
  against their user id.

Neither is reachable from a listing, and the sibling operations on the same entities
refuse it: `Delete` on another member's transaction already threw, and there was a test
saying so. These two just took a different route to the data. Both now go through the
scoped `Get`, and a rule in someone else's group reports "Rule does not exist." — the
same answer a missing one gives, because whether it exists is not the caller's to learn.

`tests/GroupSplit.API.Test/Authorization/CrossUserAccessTest.cs` now comes at fourteen
reads and writes from the outside, as a member of no shared group. Twelve already held.

### Nothing validated a patched model

The three PATCH routes — groups, rules, transactions — load the current model, apply a
`JsonPatchDocument` to it, and hand the result to the service. `AddValidation()` validates
an endpoint's *parameters*, and on those routes the parameter is the patch document, so
the model it produced reached the service checked by nothing.

Everything the create path enforces was therefore skipped on update: a required name could
be cleared, a name or description could exceed its length limit, and an amount could carry
fractions of a cent that the split arithmetic would then divide up. `PatchedModel.IsValid`
now runs the annotations on the patched model and returns a validation problem shaped like
the one the create routes produce.

Two related fixes fell out of it:

- `UpdateTransactionRequest.Amount` gained the `[MaxDecimalPlaces(2)]` the create request
  already had. Without it the new check would have had nothing to enforce on the field
  that motivated it.
- `MaxDecimalPlacesAttribute` no longer throws `OverflowException` on a value too large to
  scale. That behaviour was pinned by a test one commit earlier as surprising-but-current;
  putting the attribute on the update path gave it a reason to change, because a rejected
  amount reaching the client as a 500 rather than a 400 is the wrong answer.

## The three gaps, closed

### Endpoint tests

`tests/GroupSplit.API.Test/Base/ApiEndpointHost.cs` boots the API's own pipeline over a
`TestServer`: routing, model binding, the authorization policies, `CurrentUserMiddleware`
and the endpoint code. Two things are swapped and nothing else — Keycloak for a scheme
that authenticates whoever the test says, and Postgres for the in-memory provider the rest
of the suite already uses.

That makes the boundary testable from the outside: anonymous requests are refused on every
group, a second signed-in caller sharing no group gets a 404 for another member's
transaction rather than its contents, and a patch that clears a name or asks for fractions
of a cent comes back 400.

**One limitation, and it is worth knowing.** Body validation on the *create* routes is not
asserted there. In .NET 10 minimal-API validation is a source-generated *interceptor* on
the `AddValidation()` call site, so it exists only inside the API assembly's own
`Program.cs`; a host assembled from outside gets the registration without the interceptor
and the annotations never run. Two tests were written against it, failed, and were removed
rather than left asserting an artifact of the harness. What the annotations do is covered
by `ValidationAttributeTests`; the PATCH routes go through `PatchedModel`, which is
ordinary code and does run there.

The harness also surfaced something the service tests could not: deleting another member's
transaction is correctly refused, but refused by throwing, and with no exception-to-status
mapping in the API what reaches a client is a **500 where a 404 belongs**. That is pinned
by a test that says so.

### TokenRefreshService

`ShouldRefresh` answered `false` when the stored `expires_at` was missing or unparseable,
so an unknown expiry meant *never refresh*: the app kept presenting a token that may well
have been dead and every call to the API came back 401 with nothing saying why. Not
knowing when a token expires is not evidence that it has not, so it now refreshes.

That change needed a second one to be safe. The write-back stored
`UtcNow.AddSeconds(expires_in ?? 0)`, so a token response without `expires_in` — only
RECOMMENDED by RFC 6749 — stored an expiry of *now*, which paired with the new behaviour
would have refreshed on every single request. `ResolveExpiry` now takes `expires_in` when
it is given, falls back to the access token's own `exp` claim, and only then to a short
floor that is still in the future. The tests include the round trip: what a refresh writes
has to be something the next `ShouldRefresh` can read back.

### SeederOrderingExtensions

`tests/GroupSplit.Seeder.Test` is a new project, in the solution and in the unit-test CI
job with the other two, so its coverage counts toward the same gate. Twelve tests over
`TopologicallySort`: layering, that registration order does not decide execution order,
that a seeder waits for every dependency rather than the first, the unregistered-dependency
error, and the cycle — including a self-dependency, and a cycle off to one side, which
matters because without the check those seeders are silently dropped from the run rather
than reported.

## The wire contract between API and client

`RuleVersionDto` is the one polymorphic type that crosses the wire, and the two ends do
not use the same serializer settings: the API is a minimal API and so uses
`JsonSerializerDefaults.Web`, while the generated client is handed
`GroupSplitSerializer.Transform(new JsonSerializerOptions())` — plain defaults with a
camel-case policy on top, case-sensitive where the API is not. `RuleVersionContractTest`
covers the gap in both directions, for all three rule types, at the root and nested inside
the request that creates a rule and the response that returns one, and pins the
discriminator strings literally: renaming one is invisible in C# and breaks every client
that has not been regenerated.

The tests are deliberately **not** written with the generated client. It lives in
`GroupSplit.App.Shared` behind MudBlazor and the Blazor component stack, its typed methods
exist to stop you constructing the malformed requests the endpoint tests need, and being
generated *from* the OpenAPI document it drifts with the document rather than catching
drift. Its serializer options are the part that can actually disagree with the API, so
those are what is tested.

Writing them turned up something worth knowing: System.Text.Json writes the discriminator
only when the **static** type is the base. Serialize a concrete `PercentRuleVersionDto` and
`$type` is simply absent, and the reading end then cannot reconstruct it. That is why every
DTO carrying a rule version declares the property as `RuleVersionDto`, and nothing in C#
marks the difference — so it has a test of its own.

## A note on what the coverage figure measures

`GroupSplit.App.Shared` is excluded in `tests/coverage.config`. It is the Blazor component
library, it has no component tests of any kind, and its ~1,260 lines sit at 0%.

It is excluded because it is **unmeasured, not covered**. Until the contract tests above,
nothing loaded the assembly, so it never appeared in the report at all and the headline
figure quietly excluded it anyway. Loading one serializer helper out of it dropped the
reported number from 69.5% to 44.9% without a line of production code changing. A floor
that moves on which assemblies happen to get loaded is not a ratchet, so the exclusion is
written down rather than left to chance. Adding component tests — and taking that line back
out — is the honest fix.

Within what is measured, the API assembly is at **86.5%** line, up from 43.9%.

## Still open

- **Exception-to-status mapping.** Every guard in these services throws a bare exception
  and the API maps none of them, so a refusal a client should see as a 404 or a 400 arrives
  as a 500. The endpoint harness now demonstrates it. This is the largest remaining
  correctness gap and it is a design decision, not a cleanup.
- **Create-route validation over HTTP**, per the interceptor limitation above. Reaching it
  would mean booting the real `Program` through `WebApplicationFactory`, which needs
  Keycloak and Postgres stubbed at their registration points.
- **Blazor component tests.** `GroupSplit.App.Shared` and `GroupSplit.App.Web.Client` have
  none, which is why the former is excluded from the gate above. bUnit is the usual answer.
- **The rest of `TokenRefreshService`.** The two decisions it makes on its own are covered;
  the HTTP exchange around them — the refresh request itself, a non-success response, a
  reused refresh token — still is not, and would need a stubbed token endpoint.
- **`DistributedCacheTicketStore` and `AuthDelegatingHandler`** — 34 lines between them, no
  faults found by reading, both straightforwardly testable.

## A group with no rule

`CreateTransactionRequest` carried no group, so the API could not tell a personal
expense from a group transaction whose rule the member was never able to pick. The
create dialog lets you choose a group, fills the category select from that group's
rules, and leaves `RuleVersionId` null when there are none to offer — and the service
then fell back to the personal group's default rule. A transaction entered against
"Trip to Rome" was saved as a personal one, under a group the member never chose, with
nothing anywhere saying so.

The request now carries an optional `GroupId`, the dialog sends the group it selected,
and `TransactionService.Create` refuses the combination of a real group and no rule
version. The message distinguishes the two ways to get there — the group has no rule at
all, or it has rules and none was selected — because the fix for each is different. The
existing path, no group and no rule, still means a personal expense and is unchanged.

## The reason they never finished

Turning the Aspire tests on found a real bug, and it is worth writing down because the
symptom pointed nowhere near the cause.

Run locally, the tests produced no output in 13 minutes. The containers came up in
seconds and a `GroupSplit.API` process started, but no web-app process and no seeder
process ever appeared — which is exactly what the tests block on. Two independent
faults, one in the app and one in the test helper.

### The app: health probes redirected into a 404

The API and web app both call `MapDefaultEndpoints`, which serves `/health` and
`/alive` on an unpublished management port and 404s those paths on any other port, so
they cannot be reached from outside. Both then call `UseHttpsRedirection`.

The redirect middleware runs before routing, so it caught the probe first:

```
GET  http://localhost:53233/health      (management port, plain HTTP)
307  https://localhost:52621/health     (the public HTTPS port)
404                                     (right path, wrong port — the guard rejects it)
```

The readiness probe could therefore never pass. `api` never went healthy, so `web`
— which has `.WaitFor(backend)` — never started at all, and everything waiting on
`web` waited forever. The same trap sat in the web app for its own probe.

Only run mode shows it. Deployed, nothing gives these apps an HTTPS port, so
`UseHttpsRedirection` logs a warning and passes everything through; the AppHost hands
out an HTTPS endpoint, which switches it on. That is why the deployed stack is fine
and the local one was not, and why the bug survived unnoticed.

The fix is `UseDefaultHttpsRedirection()` in `GroupSplit.ServiceDefaults`, which
applies the redirect to everything except the health paths. Exempting by path costs
nothing: a health request on any other port is still answered with a 404 by the port
guard, whichever scheme it arrived on.

### The test helper: a command name that does not exist

`DataTest` starts the explicitly-started seeder with

```csharp
ExecuteCommandAsync("seeder", "start", …)
```

but the command Aspire registers is `resource-start`
(`KnownResourceCommands.StartCommand`) — the AppHost's own reset-and-seed command
already uses that name. An unrecognised name comes back as a **failed result rather
than an exception**, and the helper discarded the result and then waited for a
resource nothing had started. It now uses the constant and asserts on the result.

### And nothing was bounded

No `[Fact(Timeout)]`, no timeout in `xunit.runner.json` (xUnit v3 has no such key —
per-test attributes are the only lever), no `--timeout` on the runner, and no
`timeout-minutes` on either CI job, so both inherited GitHub's six-hour default. Every
`WaitFor…` was passed `TestContext.Current.CancellationToken`, which looks like
cancellation is handled but is only signalled when the whole run is cancelled.

`AppHostFixture` now bounds start-up (8 minutes, sized for cold image pulls on a
hosted runner) and every wait (3 minutes), each test carries a `Timeout`, and both CI
jobs have `timeout-minutes`. The fixture also keeps the last reported state of every
resource and prints it with any timeout, so the next stall names the resource that did
not start rather than the one that was waiting.

### What the tests found once they ran

Both of the existing assertions were stale, which is what happens to a test nobody
runs. `HomeTest` expected the page title to match `Home`; it has been `Group Split`
since `Home.razor` set it. And `DataTest` asserted nothing at all. The suite is now
three tests — the offset round-trip, the home page title, and an anonymous visitor
being offered a sign-in — and takes about 40 seconds with images cached.

## Why the job was out, and what brought it back

The job ran once on `dev` and failed in 1m42s — not a hang, which is the timeouts
doing their job, but a failure all the same:

```
net::ERR_CERT_AUTHORITY_INVALID at https://localhost:33587/
  navigating to "https://localhost:33587/", waiting until "load"
```

`DataTest` passed against real Postgres. Both Playwright tests failed at
`Page.GotoAsync`, before any assertion: `GetEndpoint("web")` hands back the HTTPS
endpoint, and Chromium would not open it because the ASP.NET Core development
certificate is not a trusted authority on a hosted runner. It is trusted on a
developer machine — `dotnet dev-certs https --trust` — which is why the same three
tests passed locally and two of them could not pass in CI.

The job is back. The certificate is now trusted on the runner rather than waved past
in the browser, which is the difference between testing the app over TLS and testing it
with verification switched off. Four things changed:

1. **CI trusts the development certificate, via Aspire.** The `integration` job installs
   the Aspire CLI — pinned to 13.5.3, the version the projects reference — and runs
   `aspire certs trust --non-interactive`. Two things have to be in place first, or the
   half of that command that matters here quietly does nothing:

   - **`certutil`, from `libnss3-tools`.** Aspire trusts the certificate for OpenSSL and
     for browsers, and the browser half goes through `certutil`. The tool is not on a
     fresh runner.
   - **Chromium's NSS database at `~/.pki/nssdb`.** `certutil` needs an existing database
     to write into. Chromium on Linux reads user certificates from there, and Playwright's
     bundled Chromium is no exception.

   On a non-interactive Linux run Aspire treats a *partially* trusted certificate —
   OpenSSL trust succeeded, NSS trust could not complete — as success rather than a
   failure. So the job does not trust its exit code: a following step greps the NSS
   database for the certificate and fails with an explicit message if it is not there,
   which puts the error at the certificate rather than several minutes later at a
   browser navigation.

   `SSL_CERT_DIR` is also extended to include `~/.aspnet/dev-certs/trust`, which is where
   `dotnet dev-certs` exports the certificate on Linux and the only place OpenSSL will
   look for it. Without that the .NET side of the stack — including the AppHost's own
   health checks, which probe the HTTPS endpoints — rejects the very certificate the
   browser now accepts.

   `IgnoreHTTPSErrors` on the browser context was the other option and is not what this
   does. It would have been one line, but it suppresses verification rather than
   establishing trust: a real TLS regression in the app would stop failing the suite, and
   it fixes nothing for the .NET half of the stack, which does not go through Playwright
   at all.

2. **The two `xUnit1069` warnings are gone.** Playwright's API takes no
   `CancellationToken`, so there is nothing to hand `TestContext.Current.CancellationToken`
   to and the rule is suppressed in `HomePageTest` with that reason written above it.
   In exchange each Playwright call now carries an explicit timeout of its own, well
   inside the `Timeout` on the test, so a stall fails with a Playwright error naming
   the operation and the selector rather than a bare xUnit timeout.

3. **A resource that fails to start now says so.** `AppHostFixture.WaitForAsync` attached
   its resource-state report only to timeouts. A resource that failed outright throws
   `DistributedApplicationException` instead, and that path lost the report entirely — the
   failure arrived as a bare stack trace naming only the resource being waited on, which
   is usually downstream of the one that actually broke. Both paths now carry the states.

4. **The `integration` job is back in `.github/workflows/ci.yml`**, with Docker, `pwsh
   … playwright.ps1 install --with-deps chromium` and a 25-minute `timeout-minutes`
   backstop. `GroupSplit.AppHost.Test` is no longer built by the unit-test job, since
   the integration job is the only place with the setup it needs.

Not yet verified on a hosted runner. The certificate trust is the documented cause of the
original failure and the suite passes locally, but locally the certificate is already
trusted, so the CI steps that establish that trust are exercised for the first time by the
first run on this branch.

## Running the integration tests locally

One AppHost at a time. The suite brings up Postgres, Keycloak and the web app under fixed
container names and a Keycloak data volume, so a second run started while one is in
flight — a `dotnet test` beside the IDE's test runner, say — takes the ports and the
volume from the first and the `web` resource fails to start. That failure now prints the
state of every resource, which is what tells it apart from a real break.
