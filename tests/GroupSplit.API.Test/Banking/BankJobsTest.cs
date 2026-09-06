using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The two bank jobs: one syncs a connection, the other asks for every active connection to
/// be synced. The sweep is what makes a missed webhook cost a day rather than forever, so
/// what it does and does not enqueue is worth pinning.
/// </summary>
public class BankJobsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly RecordingQueue _queue = new();
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IJobQueue>(_queue));
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
    }

    [Fact]
    public async Task The_sweep_enqueues_one_sync_per_active_connection_and_no_other()
    {
        var active = await LinkAsync(BankConnectionStatus.Active);
        var alsoActive = await LinkAsync(BankConnectionStatus.Active);
        await LinkAsync(BankConnectionStatus.LoginRequired);
        await LinkAsync(BankConnectionStatus.Revoked);

        await GetService<IJobHandler<SweepBankConnections>>().HandleAsync(new SweepBankConnections(), Ct);

        var queued = _queue.Jobs.OfType<SyncBankConnection>().Select(job => job.ConnectionId).Order().ToList();

        Assert.Equal(new[] { active.Id, alsoActive.Id }.Order(), queued);
    }

    [Fact]
    public async Task The_sync_job_hands_its_connection_to_the_sync_engine()
    {
        var connection = await LinkAsync(BankConnectionStatus.Active);
        _bank.Answer("cursor-1", added: [FakeBankConnector.Row("t1", 5m)]);

        await GetService<IJobHandler<SyncBankConnection>>().HandleAsync(new SyncBankConnection(connection.Id), Ct);

        Assert.Equal([null], _bank.CursorsSeen);
    }

    [Fact]
    public void The_sweep_is_registered_to_recur_daily_after_a_short_delay()
    {
        var recurring = GetService<RecurringJobs>().All.Single();

        Assert.Equal("bank.sweep-connections", recurring.JobType);
        Assert.Equal(BankingServiceExtensions.SweepPeriod, recurring.Period);
        Assert.Equal(BankingServiceExtensions.SweepDelay, recurring.InitialDelay);
    }

    private async Task<BankConnection> LinkAsync(BankConnectionStatus status)
    {
        var connection = new BankConnection
        {
            User = GetService<ICurrentUser>().User,
            Provider = FakeBankConnector.Name,
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
            LinkedAt = DateTimeOffset.UtcNow,
            Status = status
        };

        connection.Accounts.Add(new LinkedAccount
        {
            ProviderAccountId = "acc-1",
            Name = "Everyday",
            Type = "depository"
        });

        DbContext.Add(connection);
        await DbContext.SaveChangesAsync(Ct);

        return connection;
    }

    /// <summary>Keeps every job enqueued, typed, so a test can read what was asked for.</summary>
    private sealed class RecordingQueue : IJobQueue
    {
        public List<object> Jobs { get; } = [];

        public Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IJob
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }
}
