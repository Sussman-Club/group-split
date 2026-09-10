using System.Text.Json;
using Aspire.Hosting.Testing;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Plaid;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GroupSplit.AppHost.Test.Banking;

/// <summary>
/// The Plaid connector against Plaid, rather than against recorded payloads.
/// </summary>
/// <remarks>
/// Everything else that exercises this code stands something in for the provider: the unit
/// tests script a fake connector, and the connector's own tests replay payloads captured
/// once and never since. Both are the right shape for what they test, and neither can
/// notice the thing this notices — Plaid changing under us. A recorded payload agrees with
/// itself for ever.
/// <para>
/// It also reaches what an in-memory database cannot: column widths against the strings a
/// real provider actually sends, and real error codes rather than ones a fake was told to
/// throw.
/// </para>
/// <para>
/// Credentials come from the environment, and their absence skips rather than fails. That
/// is the ordinary case on a fork's pull request, where GitHub withholds secrets by
/// design, and on any machine that has not been given a sandbox key.
/// </para>
/// <para>
/// One item per run, removed in a <c>finally</c>. Sandbox items cost nothing, but the habit
/// is the one this application is built around: an item that is not handed back is an item
/// nobody can reach.
/// </para>
/// </remarks>
public class PlaidSandboxTest(AppHostFixture appHost)
{
    /// <summary>Plaid's own test institution, and the one every sandbox account has.</summary>
    private const string Institution = "ins_109508";

    /// <summary>
    /// Its own database on the stack's Postgres. The shared one is seeded and is what the
    /// other tests in this assembly read, and a sync engine writing through its own scopes
    /// cannot be wrapped in a transaction and rolled back the way those do.
    /// </summary>
    private const string Database = "groupsplit_plaid_live";

    /// <summary>
    /// The credentials, read the way the application reads them.
    /// </summary>
    /// <remarks>
    /// <c>Plaid__ClientId</c> and <c>Plaid__Secret</c> in the environment, which is
    /// ASP.NET Core's own spelling of <c>Plaid:ClientId</c> and <c>Plaid:Secret</c> — the
    /// same keys the API binds and Going.Plaid reads. Nothing here maps a name onto
    /// another one, so there is no second spelling to keep in step.
    /// </remarks>
    private static readonly IConfiguration FromEnvironment = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    private static string? ClientId => FromEnvironment[$"{PlaidConnectorOptions.SectionName}:ClientId"];

    private static string? Secret => FromEnvironment[$"{PlaidConnectorOptions.SectionName}:Secret"];

    private static readonly Guid PersonId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact(Timeout = 600_000)]
    public async Task A_bank_is_linked_synced_repaired_and_handed_back()
    {
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(Secret),
            "Plaid__ClientId and Plaid__Secret are not set, so there is no sandbox to talk to.");

        var ct = TestContext.Current.CancellationToken;

        await using var services = await ProviderAsync(ct);

        string? accessToken = null;

        try
        {
            // ------------------------------------------------------------ link
            var publicToken = await SandboxAsync("/sandbox/public_token/create",
                new { institution_id = Institution, initial_products = new[] { "transactions" } }, ct);

            Guid connectionId;

            await using (var scope = services.CreateAsyncScope())
            {
                var linked = await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                    .Link(new CreateBankConnectionRequest
                    {
                        PublicToken = publicToken.GetProperty("public_token").GetString()!
                    }, ct);

                connectionId = linked.Id;
            }

            await using (var scope = services.CreateAsyncScope())
            {
                var stored = await ConnectionAsync(scope, connectionId, ct);

                accessToken = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>()
                    .Unprotect(stored.AccessTokenCiphertext);

                Assert.Equal("First Platypus Bank", stored.InstitutionName);
                Assert.True(stored.Accounts.Count >= 10, $"Plaid reported {stored.Accounts.Count} accounts.");

                // The token is in the row, and the row is not the token.
                Assert.DoesNotContain("access-sandbox", stored.AccessTokenCiphertext, StringComparison.Ordinal);

                // Widths a real provider's strings have to fit, which the in-memory
                // provider the API tests use would accept whatever their length.
                Assert.All(stored.Accounts, account =>
                {
                    Assert.True(account.Name.Length <= 128, account.Name);
                    Assert.True(account.Subtype is null or { Length: <= 32 }, account.Subtype);
                    Assert.Equal(3, account.Currency.Length);
                });
            }

            // ------------------------------------------------------------ sync
            // Plaid pulls the history after the item is created and announces it with a
            // webhook. Nothing here has a public address for one, so this waits the way
            // the nightly sweep would.
            var imported = await SyncUntilRowsAsync(services, connectionId, ct);

            Assert.True(imported > 0, "Plaid's sandbox produced no transactions to import.");

            await using (var scope = services.CreateAsyncScope())
            {
                var rows = await RowsAsync(scope, connectionId, ct);

                Assert.All(rows, row =>
                {
                    Assert.True(row.Description.Length <= 256, row.Description);
                    Assert.True(row.MerchantName is null or { Length: <= 128 }, row.MerchantName);
                    Assert.True(row.ProviderCategoryDetailed is null or { Length: <= 96 },
                        row.ProviderCategoryDetailed);
                });

                Assert.NotNull((await ConnectionAsync(scope, connectionId, ct)).Cursor);
            }

            // ------------------------------- an account the connection does not know
            // Issue 233, against real data. Forgetting an account and its rows leaves the
            // connection in exactly the state it is in when somebody shares an account
            // through update mode: the bank has it and this does not.
            var (forgotten, dropped) = await ForgetBusiestAccountAsync(services, connectionId, ct);

            Assert.True(dropped > 0, "The busiest account had no rows, so nothing would name it.");

            await using (var scope = services.CreateAsyncScope())
            {
                Assert.Equal(SyncOutcome.Completed,
                    await scope.ServiceProvider.GetRequiredService<IBankSyncService>().SyncAsync(connectionId, ct));
            }

            await using (var scope = services.CreateAsyncScope())
            {
                var connection = await ConnectionAsync(scope, connectionId, ct);
                var back = connection.Accounts.SingleOrDefault(a => a.ProviderAccountId == forgotten);

                Assert.True(back is not null, "The account was not picked up from Plaid mid-sync.");

                var recovered = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .Set<BankTransaction>()
                    .CountAsync(row => row.LinkedAccountId == back!.Id, ct);

                Assert.Equal(dropped, recovered);

                // Nothing is missing, so nothing is asked of the person.
                Assert.False(connection.AccountsNotShared);
            }

            // --------------------------------------------------------- refresh
            await using (var scope = services.CreateAsyncScope())
            {
                var refreshed = await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                    .Refresh(connectionId, ct);

                Assert.True(refreshed.Accounts.Count >= 10);
                Assert.Equal(BankConnectionStatus.Active, refreshed.Status);
            }

            // ------------------------------------- a real ITEM_LOGIN_REQUIRED
            await SandboxAsync("/sandbox/item/reset_login", new { access_token = accessToken }, ct);

            await using (var scope = services.CreateAsyncScope())
            {
                // Not a bad gateway. The provider answered perfectly well; it said this
                // item needs its owner, and the app has a way of saying that.
                await Assert.ThrowsAsync<GroupSplit.API.Errors.ConflictException>(
                    () => scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                        .Refresh(connectionId, ct));
            }

            await using (var scope = services.CreateAsyncScope())
            {
                Assert.Equal(BankConnectionStatus.LoginRequired,
                    (await ConnectionAsync(scope, connectionId, ct)).Status);

                Assert.Equal(SyncOutcome.NotSyncable,
                    await scope.ServiceProvider.GetRequiredService<IBankSyncService>().SyncAsync(connectionId, ct));
            }

            // ---------------------------------------------------------- unlink
            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IBankConnectionService>().Unlink(connectionId, ct);
            }

            await using (var scope = services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                Assert.False(await dbContext.Set<BankConnection>().AnyAsync(ct));
                Assert.False(await dbContext.Set<LinkedAccount>().AnyAsync(ct));
                Assert.False(await dbContext.Set<BankTransaction>().AnyAsync(ct));
            }

            // The point of telling the provider before deleting the row: the item stops
            // being counted against whatever the plan counts.
            var gone = await SandboxAsync("/item/get", new { access_token = accessToken }, ct);
            Assert.True(gone.TryGetProperty("error_code", out _), "Plaid still knows the item.");

            accessToken = null;
        }
        finally
        {
            if (accessToken is not null)
            {
                await SandboxAsync("/item/remove", new { access_token = accessToken }, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The services the API would have, wired the way the API wires them, against this
    /// test's own database and the real Plaid.
    /// </summary>
    private async Task<ServiceProvider> ProviderAsync(CancellationToken ct)
    {
        await appHost.WaitForAsync(
            "db",
            (notifications, token) => notifications.WaitForResourceHealthyAsync("db", token),
            "become healthy");

        var connectionString = new NpgsqlConnectionStringBuilder(
            await appHost.Application.GetConnectionStringAsync("db", ct))
        {
            Database = Database
        }.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plaid:ClientId"] = ClientId,
                ["Plaid:Secret"] = Secret,
                ["Plaid:Environment"] = "Sandbox",
                ["Plaid:ClientName"] = "GroupSplit tests",
                ["Banking:Provider"] = PlaidConnector.Name
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<PostgreSqlAppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<AppDbContext>(provider => provider.GetRequiredService<PostgreSqlAppDbContext>());
        services.Configure<BankingOptions>(configuration.GetSection(BankingOptions.SectionName));
        services.AddBankingServices();
        services.AddPlaidConnector(configuration);
        services.AddScoped<ICurrentUser, TheOnePerson>();

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // From the model rather than the migrations: this database exists for one test
            // and is dropped at the start of the next run, so what it needs is the shape
            // the code expects, not the history of how it got there.
            await dbContext.Database.EnsureDeletedAsync(ct);
            await dbContext.Database.EnsureCreatedAsync(ct);

            dbContext.Add(new User
            {
                Id = PersonId,
                FirstName = "Sandbox",
                LastName = "Tester",
                Email = "sandbox@example.test"
            });

            await dbContext.SaveChangesAsync(ct);
        }

        return provider;
    }

    private static async Task<int> SyncUntilRowsAsync(IServiceProvider services, Guid connectionId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        while (true)
        {
            await using var scope = services.CreateAsyncScope();

            var outcome = await scope.ServiceProvider.GetRequiredService<IBankSyncService>()
                .SyncAsync(connectionId, ct);

            Assert.Equal(SyncOutcome.Completed, outcome);

            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Set<BankTransaction>()
                .CountAsync(row => row.Account.BankConnectionId == connectionId, ct);

            if (rows > 0 || DateTimeOffset.UtcNow > deadline)
                return rows;

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    /// <summary>
    /// Drops the account holding the most rows, along with those rows, and rewinds the
    /// cursor so the next run walks the same pages again.
    /// </summary>
    private static async Task<(string ProviderAccountId, int Rows)> ForgetBusiestAccountAsync(
        IServiceProvider services, Guid connectionId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = await dbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .SingleAsync(candidate => candidate.Id == connectionId, ct);

        var counts = await dbContext.Set<BankTransaction>()
            .GroupBy(row => row.LinkedAccountId)
            .Select(group => new { AccountId = group.Key, Rows = group.Count() })
            .ToListAsync(ct);

        var busiest = counts.OrderByDescending(entry => entry.Rows).First();
        var account = connection.Accounts.Single(candidate => candidate.Id == busiest.AccountId);

        dbContext.RemoveRange(dbContext.Set<BankTransaction>()
            .Where(row => row.LinkedAccountId == account.Id));

        dbContext.Remove(account);
        connection.Cursor = null;

        await dbContext.SaveChangesAsync(ct);

        return (account.ProviderAccountId, busiest.Rows);
    }

    private static Task<BankConnection> ConnectionAsync(AsyncServiceScope scope, Guid connectionId,
        CancellationToken ct) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .AsNoTracking()
            .SingleAsync(connection => connection.Id == connectionId, ct);

    private static Task<List<BankTransaction>> RowsAsync(AsyncServiceScope scope, Guid connectionId,
        CancellationToken ct) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankTransaction>()
            .Where(row => row.Account.BankConnectionId == connectionId)
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>
    /// Plaid's sandbox-only endpoints, called directly. These are the levers that put an
    /// item into a state a person would otherwise have to cause, and no connector exposes
    /// them because nothing in the application should be able to.
    /// </summary>
    private static async Task<JsonElement> SandboxAsync(string path, object body, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://sandbox.plaid.com") };

        var payload = JsonSerializer.SerializeToNode(body)!.AsObject();
        payload["client_id"] = ClientId;
        payload["secret"] = Secret;

        var response = await http.PostAsync(path,
            new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    /// <summary>Resolved per scope, the way the real one is; a shared instance would be re-inserted.</summary>
    private sealed class TheOnePerson(AppDbContext dbContext) : ICurrentUser
    {
        public User User => dbContext.Set<User>().Single(person => person.Id == PersonId);
    }
}
