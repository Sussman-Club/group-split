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
        var scheduled = await provider.GetRequiredService<IJobScheduler>().GetScheduledJobsAsync(Ct);

        // By its job rather than by being the only one: banking schedules more than one
        // thing, and a test that breaks whenever another is added tests the count.
        var sweep = Assert.Single(scheduled, job => job.Job is SweepBankConnections);
        var cron = Assert.IsType<JobSchedule.CronSchedule>(sweep.Schedule);
        Assert.Equal("0 0 * * *", cron.Expression.Value);
        Assert.Equal(TimeZoneInfo.Utc, cron.TimeZone);
    }

    /// <summary>
    /// Five minutes, not daily. Somebody is waiting on an interrupted link -- their bank
    /// says it is connected and the app does not yet agree -- and a link that has not been
    /// exchanged yet holds a public token that lasts minutes, so the first attempt has to
    /// fall inside that window.
    /// </summary>
    [Fact]
    public async Task The_pending_link_sweep_is_registered_for_every_five_minutes()
    {
        var services = new ServiceCollection();
        services.AddBankingServices();
        await using var provider = services.BuildServiceProvider();
        var initializer = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "JobScheduleInitializer");
        await initializer.StartAsync(Ct);

        var scheduled = await provider.GetRequiredService<IJobScheduler>().GetScheduledJobsAsync(Ct);

        var sweep = Assert.Single(scheduled, job => job.Job is SweepPendingBankLinks);
        var cron = Assert.IsType<JobSchedule.CronSchedule>(sweep.Schedule);
        Assert.Equal("*/5 * * * *", cron.Expression.Value);
        Assert.Equal(TimeZoneInfo.Utc, cron.TimeZone);
    }

    /// <summary>
    /// The sweep that finds links a request never managed to store. Each row holds a live
    /// provider item that belongs to no connection anybody can see, so what it does and
    /// does not queue is the difference between finishing one and stranding it.
    /// </summary>
    [Fact]
    public async Task The_pending_sweep_finishes_the_links_still_worth_trying()
    {
        var interrupted = await HeldAsync(startedMinutesAgo: 30, attempts: 0);
        var triedOnce = await HeldAsync(startedMinutesAgo: 30, attempts: 1);

        await GetService<SweepPendingBankLinksHandler>().HandleAsync(new SweepPendingBankLinks(), Ct);

        Assert.Equal(
            new[] { interrupted, triedOnce }.Order(),
            _jobs.Jobs.OfType<CompletePendingBankLink>().Select(job => job.PendingLinkId).Order());

        Assert.Empty(_jobs.Jobs.OfType<AbandonPendingBankLink>());
    }

    /// <summary>
    /// Inside the grace period the request that started the link is still running, and
    /// finishing it from underneath would have two things storing the same item.
    /// </summary>
    [Fact]
    public async Task The_pending_sweep_leaves_a_link_the_request_may_still_be_holding()
    {
        await HeldAsync(startedMinutesAgo: 1, attempts: 0);

        await GetService<SweepPendingBankLinksHandler>().HandleAsync(new SweepPendingBankLinks(), Ct);

        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public async Task The_pending_sweep_gives_up_on_a_link_that_has_had_its_attempts()
    {
        var spent = await HeldAsync(startedMinutesAgo: 30, attempts: SweepPendingBankLinksHandler.MaxAttempts);

        await GetService<SweepPendingBankLinksHandler>().HandleAsync(new SweepPendingBankLinks(), Ct);

        Assert.Equal(spent, Assert.Single(_jobs.Jobs.OfType<AbandonPendingBankLink>()).PendingLinkId);
        Assert.Empty(_jobs.Jobs.OfType<CompletePendingBankLink>());
    }

    /// <summary>
    /// One row is never both. Asking for it to be finished and given up on at once would
    /// have the give-up retiring the item at the provider while the finish is still storing
    /// a connection that names it.
    /// </summary>
    [Fact]
    public async Task The_pending_sweep_never_asks_for_one_link_to_be_both_finished_and_given_up_on()
    {
        var worthTrying = await HeldAsync(startedMinutesAgo: 30, attempts: SweepPendingBankLinksHandler.MaxAttempts - 1);
        var spent = await HeldAsync(startedMinutesAgo: 30, attempts: SweepPendingBankLinksHandler.MaxAttempts);

        await GetService<SweepPendingBankLinksHandler>().HandleAsync(new SweepPendingBankLinks(), Ct);

        Assert.Equal(worthTrying, Assert.Single(_jobs.Jobs.OfType<CompletePendingBankLink>()).PendingLinkId);
        Assert.Equal(spent, Assert.Single(_jobs.Jobs.OfType<AbandonPendingBankLink>()).PendingLinkId);
    }

    private async Task<Guid> HeldAsync(int startedMinutesAgo, int attempts)
    {
        var held = new PendingBankLink
        {
            User = GetService<ICurrentUser>().User,
            Provider = FakeBankConnector.Name,
            PublicTokenCiphertext = GetService<IAccessTokenProtector>().Protect("public-token"),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-startedMinutesAgo),
            Attempts = attempts
        };

        DbContext.Add(held);
        await DbContext.SaveChangesAsync(Ct);

        return held.Id;
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
