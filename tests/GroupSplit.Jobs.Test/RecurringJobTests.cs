using GroupSplit.Jobs.DependencyInjection;
using GroupSplit.Jobs.Recurring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GroupSplit.Jobs.Test;

public class RecurringJobTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RegistrationsAreListedOnceWhateverTheOrder()
    {
        var services = new ServiceCollection();
        services.AddJobs()
            .AddRecurringJob<Tick>(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5))
            .AddRecurringJob<OtherTick>(TimeSpan.FromDays(1), TimeSpan.FromSeconds(30));

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<RecurringJobs>().All;

        Assert.Equal([typeof(Tick), typeof(OtherTick)], registrations.Select(job => job.JobType));
        Assert.Equal(TimeSpan.FromHours(1), registrations[0].Period);
        Assert.Equal(TimeSpan.FromMinutes(5), registrations[0].InitialDelay);

        // One scheduler however many registrations there are.
        Assert.Single(provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>());
    }

    [Fact]
    public async Task AJobIsDispatchedAfterItsDelayAndThenEveryPeriod()
    {
        var clock = new FakeTimeProvider();
        var dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddJobs()
            .Dispatcher.Use(dispatcher)
            .JobsBuilder
            .AddRecurringJob<Tick>(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

        var provider = services.BuildServiceProvider();
        var scheduler = provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();

        await scheduler.StartAsync(Token);
        try
        {
            // Starting is not scheduling: StartAsync returns before ExecuteAsync has placed
            // its waits on the clock, and advancing before that point advances past nothing.
            await scheduler.Scheduled.WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(TimeSpan.FromMinutes(4));

            // A negative expectation has nothing to wait for, so it gets a settling window.
            await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
            Assert.Empty(dispatcher.Jobs);

            // Advancing releases the wait, but the scheduler resumes on the thread pool, so
            // what follows waits for the count rather than reading it on the next line.
            clock.Advance(TimeSpan.FromMinutes(1));
            await dispatcher.Reached(1).WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(TimeSpan.FromHours(1));
            await dispatcher.Reached(2).WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.All(dispatcher.Jobs, job => Assert.IsType<Tick>(job));
        }
        finally
        {
            await scheduler.StopAsync(Token);
        }
    }

    [Fact]
    public async Task AFailedDispatchDoesNotStopTheSchedule()
    {
        var clock = new FakeTimeProvider();
        var dispatcher = new RecordingDispatcher { FailNext = true };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddJobs()
            .Dispatcher.Use(dispatcher)
            .JobsBuilder
            .AddRecurringJob<Tick>(TimeSpan.FromHours(1), TimeSpan.Zero);

        var provider = services.BuildServiceProvider();
        var scheduler = provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();

        await scheduler.StartAsync(Token);
        try
        {
            await scheduler.Scheduled.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await dispatcher.Attempted(1).WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(TimeSpan.FromHours(1));
            await dispatcher.Reached(1).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally
        {
            await scheduler.StopAsync(Token);
        }
    }

    // ---- fixtures ------------------------------------------------------------------------

    public sealed record Tick : IJob;

    public sealed record OtherTick : IJob;

    /// <summary>Keeps every job dispatched, so a test can read and wait on what was asked for.</summary>
    private sealed class RecordingDispatcher : IJobDispatcher
    {
        private readonly Lock _lock = new();
        private readonly List<IJob> _jobs = [];
        private readonly List<(int Count, TaskCompletionSource Source)> _dispatched = [];
        private readonly List<(int Count, TaskCompletionSource Source)> _attempts = [];
        private int _attempted;

        public bool FailNext { get; set; }

        public IReadOnlyList<IJob> Jobs
        {
            get { lock (_lock) return _jobs.ToList(); }
        }

        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _attempted++;
                Release(_attempts, _attempted);

                if (FailNext)
                {
                    FailNext = false;
                    throw new InvalidOperationException("Scripted failure.");
                }

                _jobs.Add(job);
                Release(_dispatched, _jobs.Count);
            }

            return Task.FromResult<IJobHandle>(new CompletedHandle());
        }

        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(
            IJob<TResult> job, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A recurring job carries no result.");

        /// <summary>Completes once this many jobs have been dispatched.</summary>
        public Task Reached(int count) => Wait(_dispatched, count, () => _jobs.Count);

        /// <summary>Completes once this many dispatches have been attempted, failures included.</summary>
        public Task Attempted(int count) => Wait(_attempts, count, () => _attempted);

        private Task Wait(List<(int Count, TaskCompletionSource Source)> waiters, int count, Func<int> reached)
        {
            lock (_lock)
            {
                if (reached() >= count)
                    return Task.CompletedTask;

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((count, source));
                return source.Task;
            }
        }

        private static void Release(List<(int Count, TaskCompletionSource Source)> waiters, int reached)
        {
            foreach (var (count, source) in waiters.ToList())
            {
                if (reached >= count)
                    source.TrySetResult();
            }
        }

        private sealed class CompletedHandle : IJobHandle
        {
            public ValueTask WaitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
    }
}
