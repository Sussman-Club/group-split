using GroupSplit.Jobs.Defaults;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GroupSplit.Jobs.Test;

public class SchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FactoriesCreateValidatedInspectableSchedules()
    {
        var midnight = CronExpression.Daily();
        Assert.Equal("0 0 * * *", midnight.Value);
        Assert.Equal("30 9 * * *", CronExpression.Daily(9, 30).ToString());
        var schedule = JobSchedule.Cron(midnight, TimeZoneInfo.Utc);
        Assert.Same(midnight, schedule.Expression);
        Assert.Equal(TimeZoneInfo.Utc, schedule.TimeZone);
        Assert.Throws<ArgumentOutOfRangeException>(() => CronExpression.Daily(24));
        Assert.Throws<ArgumentOutOfRangeException>(() => CronExpression.Daily(9, 60));
        Assert.Throws<Cronos.CronFormatException>(() => CronExpression.Parse("invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobSchedule.Every(TimeSpan.Zero, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task OnceDispatchesAtItsInstantAndDefaultRemovesIt()
    {
        var (scheduler, clock, dispatcher) = Create();
        var job = new Tick();
        var scheduled = await scheduler.ScheduleAsync(job, JobSchedule.Once(clock.GetUtcNow().AddMinutes(1)), Ct);
        Assert.Same(scheduled, Assert.Single(await scheduler.GetScheduledJobsAsync(Ct)));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Empty(dispatcher.Jobs);
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Same(job, Assert.Single(dispatcher.Jobs));
        Assert.Empty(await scheduler.GetScheduledJobsAsync(Ct));
        await scheduled.CancelAsync(Ct);
        await scheduler.ProcessDueAsync(Ct);
        Assert.Single(dispatcher.Jobs);
    }

    [Fact]
    public async Task EveryUsesFirstRunAndSkipsMissedOccurrencesWithoutDrifting()
    {
        var (scheduler, clock, dispatcher) = Create();
        var start = clock.GetUtcNow();
        await scheduler.ScheduleAsync(new Tick(),
            JobSchedule.Every(TimeSpan.FromHours(1), start.AddMinutes(5)), Ct);
        clock.Advance(TimeSpan.FromMinutes(4));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Empty(dispatcher.Jobs);
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Single(dispatcher.Jobs);
        clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(20));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Equal(2, dispatcher.Jobs.Count);
        clock.Advance(TimeSpan.FromMinutes(40));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Equal(3, dispatcher.Jobs.Count);
        Assert.Single(await scheduler.GetScheduledJobsAsync(Ct));
    }

    [Fact]
    public async Task CancellationIsIdempotentAndSnapshotsDoNotChange()
    {
        var (scheduler, clock, dispatcher) = Create();
        var job = new Tick();
        var schedule = JobSchedule.Once(clock.GetUtcNow().AddDays(1));
        var first = await scheduler.ScheduleAsync(job, schedule, Ct);
        var second = await scheduler.ScheduleAsync(job, schedule, Ct);
        var snapshot = await scheduler.GetScheduledJobsAsync(Ct);
        await first.CancelAsync(Ct);
        await first.CancelAsync(Ct);
        Assert.Equal(2, snapshot.Count);
        Assert.Same(second, Assert.Single(await scheduler.GetScheduledJobsAsync(Ct)));
        clock.Advance(TimeSpan.FromDays(1));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Single(dispatcher.Jobs);
    }

    [Fact]
    public async Task FailedDispatchRetriesWithoutStoppingOtherSchedules()
    {
        var (scheduler, clock, dispatcher) = Create();
        dispatcher.FailNext = true;
        await scheduler.ScheduleAsync(new Tick(), JobSchedule.Once(clock.GetUtcNow()), Ct);
        await scheduler.ScheduleAsync(new Tick(),
            JobSchedule.Every(TimeSpan.FromMinutes(1), clock.GetUtcNow()), Ct);
        await scheduler.ProcessDueAsync(Ct);
        Assert.Equal(2, dispatcher.Attempts);
        Assert.Single(dispatcher.Jobs);
        clock.Advance(TimeSpan.FromSeconds(1));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Equal(2, dispatcher.Jobs.Count);
        Assert.Single(await scheduler.GetScheduledJobsAsync(Ct));
    }

    [Fact]
    public async Task CronUsesTheProvidedTimeZone()
    {
        var (scheduler, clock, dispatcher) = Create();
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 5, 13, 59, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await scheduler.ScheduleAsync(new Tick(), JobSchedule.Cron("0 9 * * 1", zone), Ct);
        await scheduler.ProcessDueAsync(Ct);
        Assert.Empty(dispatcher.Jobs);
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.ProcessDueAsync(Ct);
        Assert.Single(dispatcher.Jobs);
        await scheduler.ProcessDueAsync(Ct);
        Assert.Single(dispatcher.Jobs);
    }

    [Fact]
    public async Task InvalidSchedulesAndCancelledRequestsDoNotRegister()
    {
        var (scheduler, clock, _) = Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scheduler.ScheduleAsync(new Tick(), JobSchedule.Every(TimeSpan.Zero, clock.GetUtcNow()), Ct).AsTask());
        await Assert.ThrowsAsync<Cronos.CronFormatException>(() =>
            scheduler.ScheduleAsync(new Tick(), JobSchedule.Cron("invalid", TimeZoneInfo.Utc), Ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scheduler.ScheduleAsync(new Tick(), JobSchedule.Cron("0 0 30 2 *", TimeZoneInfo.Utc), Ct).AsTask());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.ScheduleAsync(new Tick(), JobSchedule.Once(clock.GetUtcNow()), cancellation.Token).AsTask());
        Assert.Empty(await scheduler.GetScheduledJobsAsync(Ct));
    }

    [Fact]
    public async Task BuilderSchedulesKeepTheirFirstRunWhenInstalledAtStartup()
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        var jobs = services.AddJobs();
        var schedule = JobSchedule.Every(TimeSpan.FromDays(1), clock.GetUtcNow().AddSeconds(30));
        jobs.Scheduler.Add(new Tick(), schedule);
        Assert.Same(jobs, services.AddJobs());
        await using var provider = services.BuildServiceProvider();
        clock.Advance(TimeSpan.FromDays(2));
        var initializer = provider.GetServices<IHostedService>().OfType<JobScheduleInitializer>().Single();
        await initializer.StartAsync(Ct);
        var registration = Assert.Single(await provider.GetRequiredService<IJobScheduler>().GetScheduledJobsAsync(Ct));
        Assert.Same(schedule, registration.Schedule);
        Assert.Single(provider.GetServices<IHostedService>().OfType<DefaultSchedulerWorker>());
    }

    [Fact]
    public async Task StandaloneSchedulesJobsThroughTheDefaultWorker()
    {
        var builder = new GroupSplit.Jobs.Standalone.JobsBuilder();
        var handler = new TickHandler();
        builder.Handlers.Add<Tick>(handler);
        await using var runtime = await builder.BuildAsync(Ct);
        await runtime.ScheduleAsync(new Tick(), JobSchedule.Once(DateTimeOffset.UtcNow), Ct);
        await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            runtime.GetScheduledJobsAsync(Ct).AsTask());
    }

    [Fact]
    public async Task WithoutDefaultsPreservesCustomSchedulerAndItsRetentionPolicy()
    {
        var custom = new RetainingScheduler();
        var builder = new GroupSplit.Jobs.Standalone.JobsBuilder();
        builder.Scheduler.Use(custom);
        builder.WithoutDefaults();
        builder.Dispatcher.Use(new Recorder());
        await using var runtime = await builder.BuildAsync(Ct);
        var registration = await runtime.ScheduleAsync(new Tick(), JobSchedule.Once(DateTimeOffset.UtcNow), Ct);
        Assert.Same(registration, Assert.Single(await runtime.GetScheduledJobsAsync(Ct)));
        Assert.True(runtime.Completion.IsCompletedSuccessfully);
        await runtime.DisposeAsync();
        Assert.False(custom.Disposed);
    }

    [Fact]
    public async Task StopRejectsNewSchedules()
    {
        var (scheduler, clock, _) = Create();
        var registration = await scheduler.ScheduleAsync(new Tick(), JobSchedule.Once(clock.GetUtcNow().AddDays(1)), Ct);
        scheduler.Stop();
        Assert.Empty(await scheduler.GetScheduledJobsAsync(Ct));
        await registration.CancelAsync(Ct);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            scheduler.ScheduleAsync(new Tick(), JobSchedule.Once(clock.GetUtcNow()), Ct).AsTask());
    }

    [Fact]
    public async Task StartupRegistrationsUseACustomScheduler()
    {
        var custom = new RetainingScheduler();
        var host = Host.CreateApplicationBuilder();
        host.Services.AddJobs().Scheduler.Use(custom)
            .Add(new Tick(), JobSchedule.Once(DateTimeOffset.UtcNow.AddDays(1)));
        using var runtime = host.Build();
        await runtime.StartAsync(Ct);
        Assert.Single(await custom.GetScheduledJobsAsync(Ct));
        await runtime.StopAsync(Ct);
    }

    [Fact]
    public void WithoutDefaultsRequiresAnExplicitScheduler()
    {
        var services = new ServiceCollection();
        services.AddJobs().WithoutDefaults();
        using var provider = services.BuildServiceProvider();
        Assert.Contains(nameof(IJobScheduler), Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IJobScheduler>()).Message);
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    private static (DefaultJobScheduler Scheduler, FakeTimeProvider Clock, Recorder Dispatcher) Create()
    {
        var clock = new FakeTimeProvider();
        var dispatcher = new Recorder();
        return (new DefaultJobScheduler(dispatcher, clock, NullLogger<DefaultJobScheduler>.Instance), clock, dispatcher);
    }

    public sealed record Tick : IJob;
    private sealed class TickHandler : IJobHandler<Tick>
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask HandleAsync(Tick job, CancellationToken cancellationToken)
        {
            Executed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Recorder : IJobDispatcher
    {
        public List<IJob> Jobs { get; } = [];
        public int Attempts;
        public bool FailNext;
        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Scripted failure"); }
            Jobs.Add(job);
            return Task.FromResult<IJobHandle>(new DefaultJobHandle(Task.CompletedTask));
        }
        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(IJob<TResult> job, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RetainingScheduler : IJobScheduler, IDisposable
    {
        private readonly List<IScheduledJob> _jobs = [];
        public bool Disposed;
        public ValueTask<IScheduledJob> ScheduleAsync(IJob job, JobSchedule schedule, CancellationToken cancellationToken = default)
        {
            IScheduledJob registration = new Registration(job, schedule);
            _jobs.Add(registration);
            return ValueTask.FromResult(registration);
        }
        public ValueTask<IReadOnlyList<IScheduledJob>> GetScheduledJobsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IScheduledJob>>(_jobs.ToArray());
        public void Dispose() => Disposed = true;
        private sealed record Registration(IJob Job, JobSchedule Schedule) : IScheduledJob
        {
            public ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        }
    }
}
