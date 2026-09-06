using System.Text.Json;
using GroupSplit.Jobs;
using GroupSplit.Jobs.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace GroupSplit.API.Test.Jobs;

/// <summary>
/// The job seam on its own, with nothing of the domain in it: a job goes in one side as a
/// typed object, crosses as JSON, and comes out the other into a handler in a scope of its
/// own. Everything the bank sync relies on for "enqueue and return" is pinned here.
/// </summary>
public class JobFrameworkTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_envelope_survives_the_wire_and_reads_as_itself()
    {
        var registry = new JobRegistry();
        registry.Register<Greet>();

        var envelope = registry.Envelope(new Greet("world", 3), new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var json = envelope.ToJson();
        var back = JobEnvelope.FromJson(json);

        Assert.Equal("test.greet", back.JobType);
        Assert.Equal(envelope.EnqueuedAt, back.EnqueuedAt);
        Assert.Equal("world", back.Payload.GetProperty("name").GetString());
        Assert.Equal(3, back.Payload.GetProperty("times").GetInt32());

        // Not an escaped string: a message in a queue console reads as the job it is.
        Assert.Contains("\"payload\":{\"name\":\"world\"", json);
    }

    [Fact]
    public async Task A_dispatched_job_reaches_its_handler_in_a_scope_of_its_own()
    {
        var seen = new Seen();
        var provider = new ServiceCollection()
            .AddSingleton(seen)
            .AddScoped<ScopeMarker>()
            .AddJob<Greet, GreetHandler>()
            .BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IJobDispatcher>();
        var registry = provider.GetRequiredService<JobRegistry>();

        var ran = await dispatcher.DispatchAsync(registry.Envelope(new Greet("a", 1), DateTimeOffset.UtcNow), Ct);
        await dispatcher.DispatchAsync(registry.Envelope(new Greet("b", 2), DateTimeOffset.UtcNow), Ct);

        Assert.True(ran);
        Assert.Equal(["a", "b"], seen.Names);
        Assert.Equal(2, seen.Scopes.Distinct().Count());
    }

    [Fact]
    public async Task A_job_nobody_here_handles_is_ignored_and_says_so()
    {
        var provider = new ServiceCollection()
            .AddJob<Greet, GreetHandler>()
            .AddSingleton<Seen>()
            .BuildServiceProvider();

        var envelope = new JobEnvelope
        {
            JobType = "from.the.future",
            Payload = JsonSerializer.SerializeToElement(new { }),
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var ran = await provider.GetRequiredService<IJobDispatcher>().DispatchAsync(envelope, Ct);

        Assert.False(ran);
    }

    [Fact]
    public void A_job_without_a_name_is_refused_at_registration_not_at_run_time()
    {
        var registry = new JobRegistry();

        var e = Assert.Throws<InvalidOperationException>(() => registry.Register<Nameless>());

        Assert.Contains("[JobName]", e.Message);
    }

    [Fact]
    public void Enqueuing_a_job_that_was_never_registered_is_refused()
    {
        var registry = new JobRegistry();

        var e = Assert.Throws<InvalidOperationException>(() =>
            registry.Envelope(new Greet("x", 1), DateTimeOffset.UtcNow));

        Assert.Contains("AddJob", e.Message);
    }

    [Fact]
    public void Two_job_types_cannot_share_a_name()
    {
        var registry = new JobRegistry();
        registry.Register<Greet>();

        Assert.Throws<InvalidOperationException>(() => registry.Register<AlsoGreet>());
    }

    [Fact]
    public async Task The_pump_drains_the_queue_and_a_failing_job_does_not_stop_the_next()
    {
        var seen = new Seen();
        var provider = new ServiceCollection()
            .AddSingleton(seen)
            .AddScoped<ScopeMarker>()
            .AddJob<Greet, GreetHandler>()
            .AddInProcessJobs()
            .BuildServiceProvider();

        var pump = provider.GetServices<IHostedService>().OfType<JobPump>().Single();
        var queue = provider.GetRequiredService<IJobQueue>();

        await pump.StartAsync(Ct);
        try
        {
            await queue.EnqueueAsync(new Greet("boom", 1), Ct);
            await queue.EnqueueAsync(new Greet("fine", 1), Ct);

            await seen.Reached("fine").WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(["boom", "fine"], seen.Names);
        }
        finally
        {
            await pump.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task A_recurring_job_is_enqueued_after_its_delay_and_then_every_period()
    {
        var clock = new FakeTimeProvider();
        var queue = new RecordingQueue();
        var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IJobQueue>(queue)
            .AddSingleton<Seen>()
            .AddScoped<ScopeMarker>()
            .AddJob<Tick, TickHandler>()
            .AddRecurringJob<Tick>(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5))
            .AddInProcessJobs()
            .BuildServiceProvider();

        var scheduler = provider.GetServices<IHostedService>().OfType<RecurringJobScheduler>().Single();

        await scheduler.StartAsync(Ct);
        try
        {
            // Starting is not scheduling: StartAsync returns before ExecuteAsync has placed
            // its waits on the clock, and advancing before that point advances past nothing.
            await scheduler.Scheduled.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            clock.Advance(TimeSpan.FromMinutes(4));

            // A negative expectation has nothing to wait for, so it gets a settling window.
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            Assert.Empty(queue.Jobs);

            // Advancing releases the wait, but the scheduler resumes on the thread pool, so
            // what follows waits for the count rather than reading it on the next line.
            clock.Advance(TimeSpan.FromMinutes(1));
            await queue.Reached(1).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            clock.Advance(TimeSpan.FromHours(1));
            await queue.Reached(2).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.All(queue.Jobs, job => Assert.IsType<Tick>(job));
        }
        finally
        {
            await scheduler.StopAsync(Ct);
        }
    }

    // ---- fixtures ------------------------------------------------------------------------

    [JobName("test.greet")]
    private sealed record Greet(string Name, int Times) : IJob;

    [JobName("test.greet")]
    private sealed record AlsoGreet(string Name) : IJob;

    [JobName("test.tick")]
    private sealed record Tick : IJob;

    private sealed record Nameless(string Name) : IJob;

    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class GreetHandler(Seen seen, ScopeMarker scope) : IJobHandler<Greet>
    {
        public Task HandleAsync(Greet job, CancellationToken ct = default)
        {
            seen.Record(job.Name, scope.Id);

            if (job.Name == "boom")
                throw new InvalidOperationException("Scripted failure.");

            return Task.CompletedTask;
        }
    }

    private sealed class TickHandler(Seen seen, ScopeMarker scope) : IJobHandler<Tick>
    {
        public Task HandleAsync(Tick job, CancellationToken ct = default)
        {
            seen.Record("tick", scope.Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps every job enqueued, so a test can read and wait on what was asked for.</summary>
    private sealed class RecordingQueue : IJobQueue
    {
        private readonly Lock _lock = new();
        private readonly List<IJob> _jobs = [];
        private readonly List<(int Count, TaskCompletionSource Source)> _waiters = [];

        public IReadOnlyList<IJob> Jobs
        {
            get { lock (_lock) return _jobs.ToList(); }
        }

        public Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IJob
        {
            lock (_lock)
            {
                _jobs.Add(job);

                foreach (var (count, source) in _waiters.ToList())
                {
                    if (_jobs.Count >= count)
                        source.TrySetResult();
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>Completes once this many jobs have been enqueued.</summary>
        public Task Reached(int count)
        {
            lock (_lock)
            {
                if (_jobs.Count >= count)
                    return Task.CompletedTask;

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, source));
                return source.Task;
            }
        }
    }

    /// <summary>What the handlers saw, and a way to wait for a particular thing to have been seen.</summary>
    private sealed class Seen
    {
        private readonly Lock _lock = new();
        private readonly List<string> _names = [];
        private readonly List<Guid> _scopes = [];
        private readonly List<(Func<bool> Condition, TaskCompletionSource Source)> _waiters = [];

        public IReadOnlyList<string> Names
        {
            get { lock (_lock) return _names.ToList(); }
        }

        public IReadOnlyList<Guid> Scopes
        {
            get { lock (_lock) return _scopes.ToList(); }
        }

        public void Record(string name, Guid scope)
        {
            lock (_lock)
            {
                _names.Add(name);
                _scopes.Add(scope);

                foreach (var (condition, source) in _waiters.ToList())
                {
                    if (condition())
                        source.TrySetResult();
                }
            }
        }

        public Task Reached(string name) => WaitFor(() => _names.Contains(name));

        private Task WaitFor(Func<bool> condition)
        {
            lock (_lock)
            {
                if (condition())
                    return Task.CompletedTask;

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((condition, source));
                return source.Task;
            }
        }
    }
}
