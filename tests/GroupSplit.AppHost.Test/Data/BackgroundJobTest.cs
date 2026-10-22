using Aspire.Hosting.Testing;
using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>Exercises durable claims and acknowledgements on independent PostgreSQL connections.</summary>
public class BackgroundJobTest(AppHostFixture appHost) : IAsyncLifetime
{
    private readonly string _schema = $"jobs_{Guid.NewGuid():N}";
    private readonly Clock _clock = new();
    private readonly BackgroundJobRegistry _registry = new BackgroundJobRegistry().Add<TestJob>("test-job");
    private string _connectionString = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A private schema also prevents the running API worker from claiming test invocations.
        await using var shared = await appHost.GetDbContextAsync();
        await shared.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{_schema}\"", Ct);
        var settings = new NpgsqlConnectionStringBuilder(
            await appHost.Application.GetConnectionStringAsync("db", Ct))
        { SearchPath = _schema, Pooling = false };
        _connectionString = settings.ConnectionString;
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript(), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var shared = await appHost.GetDbContextAsync();
        await shared.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", CancellationToken.None);
    }

    [Fact(Timeout = 120_000)]
    public async Task Enqueue_commits_or_rolls_back_with_the_business_change()
    {
        Guid rolledBackJob;
        Guid rolledBackUser;
        await using (var db = Context())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var user = new User { FirstName = "Rolled back" };
            db.Add(user);
            var job = Queue(db).Enqueue(new TestJob(user.Id));
            await db.SaveChangesAsync(Ct);
            rolledBackJob = job.Id;
            rolledBackUser = user.Id;
            await transaction.RollbackAsync(Ct);
        }

        await using var committed = Context();
        Assert.False(await committed.Set<User>().AnyAsync(row => row.Id == rolledBackUser, Ct));
        Assert.False(await committed.Set<BackgroundJob>().AnyAsync(row => row.Id == rolledBackJob, Ct));
        var kept = new User { FirstName = "Committed" };
        committed.Add(kept);
        var invocation = Queue(committed).Enqueue(new TestJob(kept.Id));
        await committed.SaveChangesAsync(Ct);

        await using var reader = Context();
        Assert.True(await reader.Set<User>().AnyAsync(row => row.Id == kept.Id, Ct));
        var stored = await reader.Set<BackgroundJob>().SingleAsync(Ct);
        Assert.Equal(invocation.Id, stored.Id);
        Assert.Equal(new TestJob(kept.Id), _registry.Deserialize(stored.Type, stored.Version, stored.Payload));
    }

    [Fact(Timeout = 120_000)]
    public async Task Concurrent_workers_claim_each_job_once()
    {
        var ids = new List<Guid>();
        await using (var db = Context())
        {
            for (var index = 0; index < 12; index++) ids.Add(Queue(db).Enqueue(new TestJob(Guid.NewGuid())).Id);
            await db.SaveChangesAsync(Ct);
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = Context();
            await db.Database.OpenConnectionAsync(Ct);
            await start.Task.WaitAsync(Ct);
            return await Store(db).Claim(Ct);
        }).ToArray();
        start.SetResult();
        var claims = (await Task.WhenAll(workers)).OfType<BackgroundJob>().ToArray();
        Assert.Equal(ids.Order(), claims.Select(row => row.Id).Order());
        Assert.Equal(12, claims.Select(row => row.LeaseId).Distinct().Count());
        Assert.All(claims, row =>
        {
            Assert.Equal(BackgroundJobStatus.Running, row.Status);
            Assert.Equal(1, row.Attempts);
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task Expired_lease_is_reclaimed_and_old_acknowledgement_cannot_finish_it()
    {
        await Enqueue();
        var first = Assert.IsType<BackgroundJob>(await Claim());
        Assert.Null(await Claim());
        _clock.Now = first.LeaseExpiresAt!.Value;
        var replacement = Assert.IsType<BackgroundJob>(await Claim());
        Assert.Equal(first.Id, replacement.Id);
        Assert.NotEqual(first.LeaseId, replacement.LeaseId);
        Assert.Equal(2, replacement.Attempts);

        await using (var oldWorker = Context())
            await Store(oldWorker).Finish(first, BackgroundJobStatus.Completed, null, Ct);
        var current = await Read(first.Id);
        Assert.Equal(BackgroundJobStatus.Running, current.Status);
        Assert.Equal(replacement.LeaseId, current.LeaseId);
        Assert.Equal(replacement.Payload, current.Payload);

        await using (var worker = Context())
            await Store(worker).Finish(replacement, BackgroundJobStatus.Completed, null, Ct);
        current = await Read(first.Id);
        Assert.Equal(BackgroundJobStatus.Completed, current.Status);
        Assert.Equal("{}", current.Payload);
        Assert.Null(current.LeaseId);
        Assert.Null(current.LeaseExpiresAt);
        Assert.Equal(_clock.Now, current.FinishedAt);
    }

    [Fact(Timeout = 120_000)]
    public async Task Failed_attempts_back_off_and_eventually_exhaust_the_retry_budget()
    {
        var id = await Enqueue();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var job = Assert.IsType<BackgroundJob>(await Claim());
            Assert.Equal(attempt, job.Attempts);
            await using (var worker = Context())
                await Store(worker).Finish(job, BackgroundJobStatus.Pending, "HandlerFailed", Ct);
            var saved = await Read(id);
            Assert.Null(saved.LeaseId);
            Assert.Equal("HandlerFailed", saved.Reason);
            Assert.Equal(job.Payload, saved.Payload);
            if (attempt == 10)
            {
                Assert.Equal(BackgroundJobStatus.Failed, saved.Status);
                Assert.Equal(_clock.Now, saved.FinishedAt);
                _clock.Now = _clock.Now.AddDays(1);
                Assert.Null(await Claim());
                break;
            }
            Assert.Equal(BackgroundJobStatus.Pending, saved.Status);
            Assert.Null(saved.FinishedAt);
            var expected = _clock.Now.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, attempt - 1)));
            Assert.Equal(expected, saved.AvailableAt);
            _clock.Now = expected.AddTicks(-10); // PostgreSQL timestamp precision is one microsecond.
            Assert.Null(await Claim());
            _clock.Now = expected;
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Repeatedly_abandoned_leases_become_terminal_without_another_execution()
    {
        var id = await Enqueue();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var job = Assert.IsType<BackgroundJob>(await Claim());
            Assert.Equal(BackgroundJobStatus.Running, job.Status);
            _clock.Now = job.LeaseExpiresAt!.Value;
        }
        var exhausted = Assert.IsType<BackgroundJob>(await Claim());
        Assert.Equal(id, exhausted.Id);
        Assert.Equal(BackgroundJobStatus.Failed, exhausted.Status);
        Assert.Equal("AttemptsExhausted", exhausted.Reason);
        Assert.Equal(_clock.Now, exhausted.FinishedAt);
        Assert.Null(await Claim());
    }

    [Fact(Timeout = 120_000)]
    public async Task Operator_work_is_durable_but_never_automatically_claimed()
    {
        Guid id;
        await using (var db = Context())
        {
            id = Queue(db).AwaitOperator("provider-item", new { ItemId = "item-to-remove" }, "ConnectorUnavailable").Id;
            await db.SaveChangesAsync(Ct);
        }
        _clock.Now = _clock.Now.AddYears(1);
        Assert.Null(await Claim());
        var saved = await Read(id);
        Assert.Equal(BackgroundJobStatus.AwaitingOperator, saved.Status);
        Assert.Equal(0, saved.Attempts);
        Assert.Equal("ConnectorUnavailable", saved.Reason);
        Assert.Contains("item-to-remove", saved.Payload);
    }

    [Fact(Timeout = 120_000)]
    public async Task Successful_handler_is_acknowledged_once_and_payload_is_erased()
    {
        var id = await Enqueue();
        var calls = 0;
        var services = WorkerServices();
        services.AddScoped<IJobExecutor>(_ => new Executor((job, _) =>
        {
            Assert.IsType<TestJob>(job);
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<object?>(null);
        }));
        await using var provider = services.BuildServiceProvider();
        var worker = Worker(provider);
        Assert.True(await worker.RunNext(Ct));
        Assert.False(await worker.RunNext(Ct));
        Assert.Equal(1, calls);
        var saved = await Read(id);
        Assert.Equal(BackgroundJobStatus.Completed, saved.Status);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal("{}", saved.Payload);
        Assert.Null(saved.Reason);
        Assert.Null(saved.LeaseId);
    }

    [Fact(Timeout = 120_000)]
    public async Task Failed_handler_does_not_persist_its_tracked_partial_business_changes()
    {
        Guid userId;
        Guid jobId;
        await using (var db = Context())
        {
            var user = new User { FirstName = "Original name" };
            db.Add(user);
            userId = user.Id;
            jobId = Queue(db).Enqueue(new TestJob(user.Id)).Id;
            await db.SaveChangesAsync(Ct);
        }
        var services = WorkerServices();
        services.AddScoped<IJobExecutor>(scope => new Executor(async (job, token) =>
        {
            var db = scope.GetRequiredService<AppDbContext>();
            var user = await db.Set<User>().SingleAsync(row => row.Id == ((TestJob)job).UserId, token);
            user.FirstName = "Partially changed";
            throw new InvalidOperationException("Unsafe exception details must not be persisted.");
        }));
        await using var provider = services.BuildServiceProvider();
        Assert.True(await Worker(provider).RunNext(Ct));
        await using var reader = Context();
        Assert.Equal("Original name", (await reader.Set<User>().SingleAsync(row => row.Id == userId, Ct)).FirstName);
        var saved = await Read(jobId);
        Assert.Equal(BackgroundJobStatus.Pending, saved.Status);
        Assert.Equal("HandlerFailed", saved.Reason);
        Assert.Equal(_clock.Now.AddSeconds(5), saved.AvailableAt);
        Assert.Null(saved.LeaseId);
        Assert.NotEqual("{}", saved.Payload);
    }

    [Theory(Timeout = 120_000)]
    [InlineData("unregistered-job", 1, "{}")]
    [InlineData("test-job", 99, "{}")]
    [InlineData("test-job", 1, "not-json")]
    public async Task Unknown_or_invalid_contract_is_retained_for_operator_without_execution(string type, int version, string payload)
    {
        Guid id;
        await using (var db = Context())
        {
            var row = new BackgroundJob { Type = type, Version = version, Payload = payload,
                CreatedAt = _clock.Now, AvailableAt = _clock.Now };
            db.Add(row);
            await db.SaveChangesAsync(Ct);
            id = row.Id;
        }
        var services = WorkerServices();
        await using var provider = services.BuildServiceProvider();
        var worker = Worker(provider);
        // No executor is registered: trying to execute an invalid contract would fail this test.
        Assert.True(await worker.RunNext(Ct));
        Assert.False(await worker.RunNext(Ct));
        var saved = await Read(id);
        Assert.Equal(BackgroundJobStatus.AwaitingOperator, saved.Status);
        Assert.Equal("InvalidJobContract", saved.Reason);
        Assert.Equal(payload, saved.Payload);
        Assert.Null(saved.LeaseId);
    }

    private PostgreSqlAppDbContext Context() => new(new DbContextOptionsBuilder<PostgreSqlAppDbContext>()
        .UseNpgsql(_connectionString).Options);

    private BackgroundJobQueue Queue(AppDbContext db) => new(db, _registry, _clock);
    private BackgroundJobStore Store(AppDbContext db) => new(db, _clock);

    private ServiceCollection WorkerServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<AppDbContext>(_ => Context());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IBackgroundJobStore, BackgroundJobStore>();
        return services;
    }

    private BackgroundJobWorker Worker(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), _registry, NullLogger<BackgroundJobWorker>.Instance);

    private async Task<Guid> Enqueue()
    {
        await using var db = Context();
        var row = Queue(db).Enqueue(new TestJob(Guid.NewGuid()));
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }

    private async Task<BackgroundJob?> Claim()
    {
        await using var db = Context();
        return await Store(db).Claim(Ct);
    }

    private async Task<BackgroundJob> Read(Guid id)
    {
        await using var db = Context();
        return await db.Set<BackgroundJob>().AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    public sealed record TestJob(Guid UserId) : IJob;

    private sealed class Executor(Func<IJob, CancellationToken, ValueTask<object?>> execute) : IJobExecutor
    {
        public ValueTask<object?> ExecuteAsync(IJob job, CancellationToken cancellationToken) => execute(job, cancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
