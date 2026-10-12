# Testing

Six test projects under [`tests/`](../tests), two scripts beside them, and two CI jobs that
run all of it. Five of the projects need nothing but the SDK; the sixth starts the whole
stack in Docker.

## The suites

| Project | What it covers | Docker |
| --- | --- | --- |
| [`GroupSplit.API.Test`](../tests/GroupSplit.API.Test) | The domain services on EF's in-memory provider, and the HTTP endpoints over a `TestServer`: routing, binding, validation, authorization, error codes, rate limits. The Plaid connector against recorded payloads. | no |
| [`GroupSplit.App.Web.Test`](../tests/GroupSplit.App.Web.Test) | The Blazor components under bUnit; the web host's sign-in, cookies, token store, forwarders and security headers; client-side state; the wire contract between the API and the generated client. | no |
| [`GroupSplit.Cli.Test`](../tests/GroupSplit.Cli.Test) | The CLI's commands, run in-process against a stub HTTP server: output, exit codes, auth, configuration, the confirmation protocol, and every documented example. | no |
| [`GroupSplit.Jobs.Test`](../tests/GroupSplit.Jobs.Test) | The background-job library: handler registration, processing, scheduling, and the standalone runtime. | no |
| [`GroupSplit.Seeder.Test`](../tests/GroupSplit.Seeder.Test) | The seeders and the order they run in, the Keycloak admin client, and the realm worker's definition and reconciler. | no |
| [`GroupSplit.AppHost.Test`](../tests/GroupSplit.AppHost.Test) | The stack under Aspire: queries, constraints and races on real Postgres, Keycloak, mail, webhooks, and the web app in Chromium through Playwright. | yes |

All six are xUnit v3 on Microsoft Testing Platform, with Moq where something has to be
stood in for.

## Running them

[`global.json`](../global.json) makes Microsoft Testing Platform the runner, so `dotnet test`
takes the project with `--project`, and options for the test application itself go after
`--`. Options that belong to `dotnet test` (`-c`, `--no-build`, `--results-directory`) stay
in front of it. These are the suites the unit job runs, and the CSS check beside them:

```bash
dotnet test --project tests/GroupSplit.API.Test
dotnet test --project tests/GroupSplit.App.Web.Test
dotnet test --project tests/GroupSplit.Cli.Test
dotnet test --project tests/GroupSplit.Jobs.Test
dotnet test --project tests/GroupSplit.Seeder.Test
python3 tests/check-css.py src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/app.css
```

`GroupSplit.AppHost.Test` has its own setup, in [The integration tests](#the-integration-tests).

### One class or one test

xUnit's simple filters, after the `--`. They take fully qualified names, with `*` allowed at
either end:

```bash
dotnet test --project tests/GroupSplit.API.Test -- --filter-class GroupSplit.API.Test.Group.GroupSettleTest
dotnet test --project tests/GroupSplit.API.Test -- --filter-method "*.Settle_CreatesOneTransfer"
dotnet test --project tests/GroupSplit.App.Web.Test -- --filter-namespace GroupSplit.App.Web.Test.Components
```

The same flag given twice is an OR. The `--filter-not-*` forms exclude. Simple filters,
`--filter-query` and the VSTest-style `--filter` cannot be mixed in one run.

### Coverage

The way CI measures it: each unit suite writes a Cobertura file, ReportGenerator merges them,
and the gate reads the merged figure. From the repository root:

```bash
dotnet test --project tests/GroupSplit.API.Test -c Release --results-directory TestResults \
  -- --coverage --coverage-settings tests/coverage.config \
     --coverage-output-format cobertura --coverage-output api.cobertura.xml
# ...the same for the other four unit suites, each with its own --coverage-output

dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.5.11
reportgenerator -reports:'TestResults/*.cobertura.xml' -targetdir:TestResults/coverage \
  -reporttypes:'Cobertura;Html'
python3 tests/coverage-gate.py TestResults/coverage/Cobertura.xml 70 60
```

`TestResults/coverage/index.html` is the report to browse. The two numbers on the gate are the
floors in [`ci.yml`](../.github/workflows/ci.yml); pass whatever it holds today.

## What CI checks

[`ci.yml`](../.github/workflows/ci.yml) runs on every pull request into `dev` or `main`, and
[`deploy.yml`](../.github/workflows/deploy.yml) calls it and waits, so a red build does not
ship. It has two jobs. How a deploy runs after it is in [deployment.md](deployment.md).

### Build and unit tests

`build-test`, with a 20-minute limit. In order, it:

- restores and builds the five unit suites and the migrations project in Release;
- runs [`check-css.py`](#the-css-check) over the shared bundle;
- runs the deploy scripts' own test, `.github/scripts/classify-komodo-failure.test.sh`;
- packs the CLI as a .NET tool, which catches packaging faults a build does not;
- builds the web app in Debug, the configuration `aspire do` deploys from, where some build
  steps exist only in Debug;
- runs the five suites with a TRX report and Cobertura coverage each;
- merges the coverage with ReportGenerator and writes the summary onto the run's page;
- enforces the coverage floors;
- uploads `TestResults/` as the `test-results` artifact, kept seven days.

`GroupSplit.AppHost.Test` is neither built nor run here.

### The coverage gate

[`coverage-gate.py`](../tests/coverage-gate.py) reads the line and branch rates off the
merged report and fails the build when either is under its floor. The floors are
`COVERAGE_LINE_FLOOR` and `COVERAGE_BRANCH_FLOOR` in `ci.yml`, at 70 and 60. They are a
ratchet: each was set a few points under what was measured on the day, so that
a change in what the in-memory provider happens to execute cannot fail a build on its own.
Raise them when coverage goes up. Never lower one to turn a red build green.

What counts is set in [`coverage.config`](../tests/coverage.config), which every run passes
with `--coverage-settings`; nothing picks it up by itself. It measures the `GroupSplit.*`
assemblies, less:

- the test assemblies and `GroupSplit.Data.PostgreSQL.Migrations`;
- every source file under `obj/`, which is where the OpenAPI and validation generators and
  the Razor compiler write. Razor maps a component's lines back to its `.razor` file, so
  the components themselves are still measured;
- anything marked `[ExcludeFromCodeCoverage]` or `[GeneratedCode]`.

Without the `obj/` filter the API's generated code, about four times the size of the
hand-written code beside it, would sit at 0% and move the figure whenever a generator
changed.

Never write two hyphens in a row inside an XML comment in `coverage.config`. The file stops
parsing, and the only symptom is every project reporting "Zero tests ran" with MTP exit code
5.

Only the unit job is gated. The integration job collects coverage too, into its artifact,
but no floor applies to it, so code that only `GroupSplit.AppHost.Test` reaches counts toward
nothing.

`GroupSplit.App.Shared`, the Razor class library holding the app's pages and components, was
excluded until #326 and is now gated like everything else. On the day it went in, its
`.razor` files and services measured about 77% of lines and 69% of branches, and the merged
figure stayed well above both floors.

### The CSS check

[`check-css.py`](../tests/check-css.py) takes `app.css`, which is an import manifest, follows
its `@import` graph, and fails when the same selector list appears twice in the same context:
the document root, or the same `@media` or `@supports` block, in one file or across two.
Lists compare after whitespace is normalised and their selectors sorted, so `a, b` and `b, a`
collide. Keyframe steps are ignored. A failure prints both locations; an unreadable file or
a cyclic import exits 2. It is plain Python, so the repository carries no Node toolchain for
it.

### The integration job

`integration`, with a 25-minute limit as a backstop for hangs the suite's own timeouts do not
reach. It:

- pulls the stack's Docker Hub images through `mirror.gcr.io`, by name and with retries,
  because a hosted runner shares its address and Docker Hub's anonymous pull limit with
  other jobs;
- reads the Aspire CLI version from the AppHost's SDK and installs that CLI;
- builds `GroupSplit.AppHost.Test` and the migrations project in Release;
- installs Playwright's Chromium with `--with-deps`;
- trusts the ASP.NET Core development certificate with `aspire certs trust`, after
  installing `certutil` and creating Chromium's NSS database, and adds the export directory
  to `SSL_CERT_DIR` so the .NET processes trust it too;
- checks the certificate is in the NSS database, because Aspire reports partial trust as
  success on a non-interactive Linux run;
- runs the suite with TRX and coverage, and uploads `integration-test-results`.

## The integration tests

`GroupSplit.AppHost.Test` needs Docker running, Playwright's Chromium, and a trusted ASP.NET
Core development certificate (`dotnet dev-certs https --trust`), which a machine that has run
the app already has. The Playwright install script is written into the build output, so build
first:

```bash
dotnet build tests/GroupSplit.AppHost.Test
pwsh tests/GroupSplit.AppHost.Test/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --project tests/GroupSplit.AppHost.Test
```

Run one AppHost at a time. The suite shares the data volumes, ports and container names a
normal run uses, so starting it beside a running app, or beside the IDE's test runner, makes
`web` fail to start. That failure prints every resource's state, which is what tells it apart
from a real break. A full disk looks the same: Docker's disk image grows with every run and
does not shrink by itself.

### What they start

[`AppHostFixture`](../tests/GroupSplit.AppHost.Test/Base/AppHostFixture.cs) is an assembly
fixture: one stack for the whole run, built from the real AppHost with Aspire's
`DistributedApplicationTestingBuilder`. That is Postgres, Redis, RustFS, Keycloak and its realm
worker, Mailpit, the migrations, the API and the web app. The seeder waits for an explicit
start; a test that needs the demo data calls `appHost.SeedAsync()`, which starts it once per
run.

It differs from a normal run in four places:

- No dev tunnel. A tunnel needs the `devtunnel` CLI and a signed-in account, and the API
  would wait on one that cannot start, taking the rest of the stack with it.
- The provider parameters are forced, as in
  [What they take from your user secrets](#what-they-take-from-your-user-secrets).
- The web app runs `RenderMode=ServerFirst`, the mode a deployment runs, where
  `appsettings.Development.json` says `WebAssemblyFirst`.
- Keycloak's access-token lifespan is shortened, once for the run.

Nothing waits for ever. Start-up gets 8 minutes, sized for image pulls on a cold runner, and
each `WaitForAsync` gets 3. A timeout, or a resource that fails outright, reports the last
state of every resource and the last 40 log lines of any that failed or exited. The resource
a test was waiting on is usually downstream of the one that broke, so read the report rather
than the stack trace.

`PlaidSandboxTest` talks to Plaid's real sandbox. It reads `Plaid__ClientId` and
`Plaid__Secret` from the test process's environment, not from user secrets, and skips without
them. CI does not set them, so it skips there. See [bank-sync.md](bank-sync.md) for the
sandbox itself.

### A dashboard for the integration tests

The fixture starts the AppHost without a dashboard, which leaves a test stopped at a
breakpoint, or a stack that will not come up, with nothing to look at but the resource states
the fixture prints. Set `GROUPSPLIT_TEST_DASHBOARD=1` and it runs the Aspire dashboard beside
the stack, on loopback ports of its own choosing rather than the fixed ones a normal run's
dashboard takes, and writes the sign-in URL to standard output once the application has
started:

```bash
GROUPSPLIT_TEST_DASHBOARD=1 dotnet test --project tests/GroupSplit.AppHost.Test
```

The URL carries the dashboard's browser token, which is why it is opt-in and never set in CI.
Nothing else about the run changes: a wait on a resource that fails still stops rather than
waiting for somebody to fix it from the dashboard. The dashboard goes away with the run.

### What they take from your user secrets

The suite runs the same AppHost, so it loads the same user secrets. It keeps the ones that
describe state your machine already holds: the Keycloak admin, and the generated database,
cache and storage passwords, which have to match the data volumes a normal run has
initialised. The fixture fills the Keycloak admin in only where nothing else has.

Every provider parameter (Azure OpenAI, Veryfi, Google sign-in, Plaid) is set by
`AppHostFixture.Parameters` and passed on the AppHost's command line. That is the last
source the host loads, so it outranks appsettings, user secrets and `Parameters__…`
environment variables, and a local run never reaches a real provider on your key. Until PR
#396 these were filled in only where nothing else had a value, so user secrets won: a local
run sent receipts to the developer's own Azure OpenAI deployment (#331), and
`OpenAIConfigurationTest` failed locally while passing in CI. If a secret ever gets through
again, `FixtureParameterTest` fails beside it, naming the parameter but never its value.

## Writing a test

### Where it belongs

By what could break:

| If it is about | It goes in | As |
| --- | --- | --- |
| A rule the services enforce: how an expense divides, who owes whom, who may see or change what | `GroupSplit.API.Test` | a subclass of `ApiUnitTest` |
| What a client sees over HTTP: a status, an error `code`, a validation problem, an anonymous request refused, a rate limit | `GroupSplit.API.Test` | a test over `ApiEndpointHost` |
| Anything that depends on Postgres: a query that has to translate, a constraint, an index, a transaction, two writers racing | `GroupSplit.AppHost.Test` | a case in `QueryTranslationTest` or `GroupSchemaTest`, or a class of its own |
| A sign-in, a redirect through Keycloak, a mail, anything a browser has to run | `GroupSplit.AppHost.Test` | a subclass of `WebPageTest` |
| What a component renders and what it asks the server for | `GroupSplit.App.Web.Test` | a subclass of `ComponentTest` |
| The web host's own wiring: cookies, tokens, forwarders, headers | `GroupSplit.App.Web.Test` | a test over a `TestServer`; `SignInHost` for sign-in |
| Whether the generated client reads what the API writes | `GroupSplit.App.Web.Test/Contract` | a serializer round trip |
| A CLI command, its output or its exit code | `GroupSplit.Cli.Test` | `Cli.RunAsync` against a `StubApi` |
| A seeder, the realm definition or the realm worker | `GroupSplit.Seeder.Test` | a plain class |
| The job library | `GroupSplit.Jobs.Test` | a plain class |

Somebody outside a group being refused is a service test like any other;
[`CrossUserAccessTest`](../tests/GroupSplit.API.Test/Authorization/CrossUserAccessTest.cs)
collects them. The error codes an endpoint test asserts on are catalogued in
[errors.md](errors.md).

### The in-memory provider is not the database we ship

`GroupSplit.API.Test` runs on `Microsoft.EntityFrameworkCore.InMemory`, a fresh database per
test. Production runs Postgres through Npgsql, and the two disagree in ways a unit test cannot
see:

- The in-memory provider evaluates whatever it cannot translate, where Npgsql refuses it. A
  query that 500s in production passes here.
- It enforces no unique index, foreign key, check constraint or partial index, and applies no
  referential action.
- It applies no store default. A column the database fills has to be written by the test, as
  `ApiUnitTest.JoinGroup` writes `JoinedAt`.
- It has no transactions. The fixtures make them a no-op rather than an error.

So high line coverage on a service says its branches ran, not that its queries work. This has
already happened once: `GET /invitations` ordered by a member of a projected record, the
in-memory provider sorted the objects it had already built, the unit tests passed, and Npgsql
could not translate the constructor call, so the endpoint 500ed on its first real request.
[`QueryTranslationTest`](../tests/GroupSplit.AppHost.Test/Data/QueryTranslationTest.cs) runs
the production services over the real database and executes every listing, filter, sort key
and projection the app builds. It asserts nothing about the rows: a query that does not
translate throws before it returns anything. Add a case to it whenever you add a listing.
[`GroupSchemaTest`](../tests/GroupSplit.AppHost.Test/Data/GroupSchemaTest.cs) does the same
for constraints, writing past the services straight at the table and expecting the database
to refuse.

Keep the unit test as well. It is fast and says what the service means; the AppHost case says
the query runs.

### API tests

A service test is a class with the fixture in its primary constructor:

```csharp
public class GroupSettleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
```

[`ApiUnitTest`](../tests/GroupSplit.API.Test/Base/ApiUnitTest.cs) gives each test its own
service provider, cloned from a template the assembly fixture builds with `AddDomainServices()`,
the production registration list; its own in-memory database; and a signed-in user with a
random subject. `GetService<T>()` resolves a service, `CreateNewUser()` provisions a second
account, `JoinGroup` puts people in a group without the invitation flow, and
`CreateCategory` and `CreateEvenCategory` set up a category with its split. Override
`ConfigureTestServices` to replace a registration.

An endpoint test starts
[`ApiEndpointHost`](../tests/GroupSplit.API.Test/Base/ApiEndpointHost.cs) in
`InitializeAsync` and disposes it in `DisposeAsync`. It is the API's real pipeline, in the
order `Program.cs` builds it, with two substitutions: an authentication scheme that signs in
whoever the test names, and the in-memory provider. `Client` is a signed-in caller,
`ClientForAnotherUser()` a second account sharing no group with it, `ClientFor(subject, email)`
a caller of the test's choosing, and `AnonymousClient()` nobody. `StartAsync(services => …)`
replaces a registration, such as a bank connector; `Logs` holds everything the API logged.

Both hosts are only honest while they match production. A service registered in `Program.cs`
belongs in `AddDomainServices()`, or in the `AddApi…` extension it belongs with, which the
hosts call too. A middleware added to `Program.cs` has to be added to `ApiEndpointHost` in the
same position, because that one is a copy.

The outside world is stood in for: object storage by a mocked `IAmazonS3`, Keycloak's admin
API by `FakeKeycloakUserRemoval`, Plaid by `FakeBankConnector` or by `PlaidTestServer`
replaying the recorded payloads in `Banking/Plaid/Payloads`, and the clock by
`FakeTimeProvider`.

### Component tests

[`ComponentTest`](../tests/GroupSplit.App.Web.Test/Components/ComponentTest.cs) is bUnit's
`BunitContext` with what every component needs before it can render: MudBlazor's services, a
loose JS runtime (every JS call in the app is decoration), the error presenter, the clock,
the change notifier, and mocks that answer "nothing" for the clients most components touch in
passing. Register the mocked generated client the test is about in its constructor. The later
registration is the one resolved, so a test that cares about one of the defaults registers its
own. `ReaderAt(offset)` fixes the reader's time zone. Assert on what the component asked the
mocked client for and on what it rendered.

A dialog needs `MudPopoverProvider` and `MudDialogProvider` rendered before it is opened
through `IDialogService`;
[`SettleDialogTest`](../tests/GroupSplit.App.Web.Test/Components/SettleDialogTest.cs) shows
the shape.

Do not await `dialog.Result` inside a click handler. The page tests mock `IDialogService` and
hand back a `DialogReference` whose `Result` never completes unless the test dismisses it, and
bUnit's click waits for the handler, so the whole suite hangs. It normally finishes in
seconds, and a hung run that gets killed still prints partial totals, so read the exit code
rather than the count. In the component, open the dialog, then pass the reference to a method
that awaits `Result` and discard its task, as `Transactions.razor` does:

```csharp
_ = ForgetExpenseWhenClosedAsync(dialog);
```

When in doubt, give the run a ceiling with the runner's own `--timeout`:

```bash
dotnet test --project tests/GroupSplit.App.Web.Test -- --timeout 5m
```

[`SharedServiceCompositionTest`](../tests/GroupSplit.App.Web.Test/State/SharedServiceCompositionTest.cs)
builds the app's container with `ValidateOnBuild`. When it fails, something is registered
without a dependency it takes; component tests register their own mocks and will not notice.

### CLI tests

[`Cli.RunAsync`](../tests/GroupSplit.Cli.Test/Cli.cs) runs the real command tree in-process
with captured writers and returns the exit code, stdout and stderr; `.Json` parses stdout, and
`.Error` reads the error envelope off the last JSON line of stderr. Those three are the CLI's
contract, set out in [cli.md](cli.md), and what a test asserts on.

[`StubApi`](../tests/GroupSplit.Cli.Test/StubApi.cs) is an `HttpListener` on a free loopback
port, so the generated client runs for real: paths, bearer header, status handling, problem
details. `Returns(path, body, status)` sets an answer, and `Requests` records each request's
method, path, query, authorization and body, for asserting what a write sent and that a filter
reached the query string.

[`EnvironmentFixture`](../tests/GroupSplit.Cli.Test/EnvironmentFixture.cs) points the config and
credential directories at a scratch directory and clears every `GROUPSPLIT_*` variable, so a
test sees a clean machine rather than your own sign-in. Environment variables are
process-wide, so a class using it goes in `[Collection(EnvironmentCollection.Name)]`, which
does not run in parallel.

Two tests read the source rather than a command:

- [`ConfirmCommandTests`](../tests/GroupSplit.Cli.Test/ConfirmCommandTests.cs) runs every
  confirmation gate twice, once with `--yes` and once by re-running the `confirmCommand` the
  prompt handed back, and compares the writes the server received. A new
  `Confirmation.Require(context, action: "…")` has to be added to its `Gates` table;
  `Every_gate_in_the_cli_is_in_the_table` scans `src/GroupSplit.Cli` and fails until it is.
- [`DocumentedExampleTests`](../tests/GroupSplit.Cli.Test/DocumentedExampleTests.cs) parses
  every `groupsplit` command in a `bash` block of `docs/cli.md` and `skills/` against the real
  command tree, with placeholders such as `<group-id>` and `$G` turned into guids. An example
  with an option the command does not have fails the build. Fence anything that is not meant
  to parse with another language.

### AppHost tests

Take the fixture in the primary constructor, `(AppHostFixture appHost)`, and wait for a
resource with `appHost.WaitForAsync(…)` rather than on `ResourceNotifications` directly: that
is where the timeout and the resource report come from. The test's own
`TestContext.Current.CancellationToken` is cancelled only when the whole run is.

Every test carries `[Fact(Timeout = …)]`, in milliseconds; xUnit v3 has no runner-wide
default. Playwright takes no cancellation token, so each Playwright call gets its own
`Timeout`, well inside the test's, and the browser test files suppress `xUnit1069` for that
reason. A stall then fails with a Playwright error naming the operation and the selector.

Browser tests derive from [`WebPageTest`](../tests/GroupSplit.AppHost.Test/Base/WebPageTest.cs),
Playwright's `PageTest`, which waits for `web` to be healthy before the page opens. It sets
`IgnoreHTTPSErrors` on the browser context, so Chromium accepts the development certificate
whatever the machine trusts; the .NET side of the stack, including the AppHost's health checks
on the HTTPS endpoints, does not, which is why the certificate still has to be trusted.

The Keycloak realm is shared by every test in the run. `appHost.Keycloak` creates accounts and
tokens; a test that changes the realm itself changes it for everything running beside it.

A new provider parameter in the AppHost goes into `AppHostFixture.Parameters`, with a value
that switches the provider off or points it nowhere. `FixtureParameterTest` only checks the
parameters already in that list, so one left out reads from user secrets again.

### Everywhere

- Pass `TestContext.Current.CancellationToken` to anything that takes a token; the xUnit
  analyzers warn when a test does not.
- Name a test as a sentence about behaviour, `A_bank_is_linked_synced_repaired_and_handed_back`
  rather than `Link_Works`. Older files use `Method_Outcome`; new tests use sentences.
- Say in the doc comment why the test exists: the defect, the issue number, the case that got
  through. Most of the suite does, and it is what tells the next person whether a failure
  means the test or the code is wrong.
- Assert on the identity of what you expect, not on a count. Every account has a personal
  group with its own rows, so a count can include things the test never made.
- Cases that differ only by input are a `[Theory]`.

### A new test project

It needs `<OutputType>Exe</OutputType>`, `xunit.v3`, `xunit.runner.json`, and both MTP
extensions, `Microsoft.Testing.Extensions.CodeCoverage` and
`Microsoft.Testing.Extensions.TrxReport`. Without the TRX extension the test application
rejects CI's `--report-trx` with exit code 5. Add it to `group-split.slnx`, and to the
restore, build and test steps of `build-test` in `ci.yml` with its own `--coverage-output`.
Until then CI does not run it and its coverage counts toward no floor.
