using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The two bank jobs: one syncs a connection, the other asks for every active connection to
/// be synced. The sweep is what makes a missed webhook cost a day rather than forever, so
/// what it does and does not dispatch is worth pinning.
/// </summary>
public class BankJobsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly RecordingDispatcher _jobs = new();
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IJobDispatcher>(_jobs));
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
    }

    [Fact]
    public async Task The_sweep_dispatches_one_sync_per_active_connection_and_no_other()
    {
        var active = await LinkAsync(BankConnectionStatus.Active);
        var alsoActive = await LinkAsync(BankConnectionStatus.Active);
        await LinkAsync(BankConnectionStatus.LoginRequired);
        await LinkAsync(BankConnectionStatus.Revoked);

        await GetService<SweepBankConnectionsHandler>().HandleAsync(new SweepBankConnections(), Ct);

        var dispatched = _jobs.Jobs.OfType<SyncBankConnection>().Select(job => job.ConnectionId).Order().ToList();

        Assert.Equal(new[] { active.Id, alsoActive.Id }.Order(), dispatched);
    }

    [Fact]
    public async Task The_sync_job_hands_its_connection_to_the_sync_engine()
    {
        var connection = await LinkAsync(BankConnectionStatus.Active);
        _bank.Answer("cursor-1", added: [FakeBankConnector.Row("t1", 5m)]);

        await GetService<SyncBankConnectionHandler>().HandleAsync(new SyncBankConnection(connection.Id), Ct);

        Assert.Equal([null], _bank.CursorsSeen);
    }

    [Fact]
    public async Task The_sweep_is_registered_for_midnight_utc()
    {
        var services = new ServiceCollection();
        services.AddBankingServices();
        await using var provider = services.BuildServiceProvider();
        var initializer = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "JobScheduleInitializer");
        await initializer.StartAsync(Ct);
        var scheduled = Assert.Single(await provider.GetRequiredService<IJobScheduler>().GetScheduledJobsAsync(Ct));
        Assert.IsType<SweepBankConnections>(scheduled.Job);
        var cron = Assert.IsType<JobSchedule.CronSchedule>(scheduled.Schedule);
        Assert.Equal("0 0 * * *", cron.Expression.Value);
        Assert.Equal(TimeZoneInfo.Utc, cron.TimeZone);
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

    /// <summary>Keeps every job dispatched, typed, so a test can read what was asked for.</summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        public List<IJob> Jobs { get; } = [];

        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken ct = default)
        {
            Jobs.Add(job);
            return Task.FromResult<IJobHandle>(new CompletedHandle());
        }

        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(IJob<TResult> job, CancellationToken ct = default) =>
            throw new NotSupportedException("No bank job carries a result.");

        private sealed class CompletedHandle : IJobHandle
        {
            public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
    }
}
