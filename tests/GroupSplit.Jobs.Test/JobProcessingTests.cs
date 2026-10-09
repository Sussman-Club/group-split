using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GroupSplit.Jobs.Test;

public class JobProcessingTests
{
    [Fact]
    public async Task ResultsCanBeReadRepeatedlyAndWaitedOn()
    {
        using var host = CreateHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var handle = await dispatcher.DispatchAsync(new ResultJob("answer"), TestContext.Current.CancellationToken);
        await handle.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("answer", await handle.GetResultAsync(TestContext.Current.CancellationToken));
        Assert.Equal("answer", await handle.GetResultAsync(TestContext.Current.CancellationToken));
        var nullHandle = await dispatcher.DispatchAsync(new ResultJob(null), TestContext.Current.CancellationToken);
        Assert.Null(await nullHandle.GetResultAsync(TestContext.Current.CancellationToken));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VoidJobsUseRuntimeTypeAndRunInSeparateScopes()
    {
        using var host = CreateHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var ids = new List<Guid>();
        IJob first = new VoidJob(ids);
        await (await dispatcher.DispatchAsync(first, TestContext.Current.CancellationToken)).WaitAsync(TestContext.Current.CancellationToken);
        await (await dispatcher.DispatchAsync(new VoidJob(ids), TestContext.Current.CancellationToken)).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailureAndCancellationReachHandlesWithoutStoppingWorker()
    {
        using var host = CreateHost();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var failed = await dispatcher.DispatchAsync(new ResultJob("fail"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.GetResultAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(TestContext.Current.CancellationToken).AsTask());
        var cancelled = await dispatcher.DispatchAsync(new ResultJob("cancel"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.GetResultAsync(TestContext.Current.CancellationToken).AsTask());
        var next = await dispatcher.DispatchAsync(new ResultJob("ok"), TestContext.Current.CancellationToken);
        Assert.Equal("ok", await next.GetResultAsync(TestContext.Current.CancellationToken));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancellingOneWaitDoesNotCancelJobOrOtherWaiters()
    {
        using var host = CreateHost();
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var handle = await dispatcher.DispatchAsync(new ResultJob("ok"), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelledWait = handle.GetResultAsync(cancellation.Token).AsTask();
        var otherWait = handle.GetResultAsync(TestContext.Current.CancellationToken).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ok", await otherWait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShutdownCancelsPendingJobsAndRejectsNewDispatches()
    {
        using var host = CreateHost();
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await dispatcher.DispatchAsync(new BlockingJob(started), TestContext.Current.CancellationToken);
        var pending = await dispatcher.DispatchAsync(new ResultJob("pending"), TestContext.Current.CancellationToken);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.GetResultAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            () => dispatcher.DispatchAsync(new ResultJob("late"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failure_nobody_waits_for_is_logged_with_the_job_and_its_inputs()
    {
        var logs = new CapturedLogs();
        using var host = CreateHost(logs);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();

        // Dispatched and dropped, the way a webhook asks for a sync.
        _ = await dispatcher.DispatchAsync(new ResultJob("fail"), TestContext.Current.CancellationToken);

        var entry = await logs.WaitForAsync(log => log.Exception is InvalidOperationException);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("ResultJob", entry.Message);
        Assert.Contains("Value = fail", entry.Message);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_cancelled_while_the_host_runs_is_logged_as_a_failure_and_handed_back_as_one()
    {
        var logs = new CapturedLogs();
        using var host = CreateHost(logs);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();

        // What an HttpClient timeout looks like from inside a handler.
        var timedOut = await dispatcher.DispatchAsync(new ResultJob("timeout"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<TaskCanceledException>(
            () => timedOut.GetResultAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.IsType<TimeoutException>(error.InnerException);
        var entry = await logs.WaitForAsync(log => log.Exception is TaskCanceledException);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("not a shutdown", entry.Message);
        Assert.Contains("Value = timeout", entry.Message);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_stopped_by_shutdown_is_noted_rather_than_logged_as_a_failure()
    {
        var logs = new CapturedLogs();
        using var host = CreateHost(logs);
        var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await dispatcher.DispatchAsync(new BlockingJob(started), TestContext.Current.CancellationToken);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestContext.Current.CancellationToken).AsTask());
        var entry = await logs.WaitForAsync(log => log.Message.Contains("BlockingJob"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("stopped by shutdown", entry.Message);
        Assert.DoesNotContain(logs.Entries, log => log.Category.EndsWith("DefaultJobWorker") && log.Level >= LogLevel.Warning);
    }

    private static IHost CreateHost(CapturedLogs? logs = null)
    {
        var host = Host.CreateApplicationBuilder();
        if (logs is not null)
            host.Logging.AddProvider(logs);
        host.Services.AddScoped<ScopeIdentity>();
        var jobs = host.Services.AddJobs();
        Assert.Same(jobs, host.Services.AddJobs());
        jobs.Handlers.AddJobHandler<ResultJob, string?, ResultHandler>();
        jobs.Handlers.AddJobHandler<VoidJob, VoidHandler>();
        jobs.Handlers.AddJobHandler<BlockingJob, BlockingHandler>();
        return host.Build();
    }

    public sealed record ResultJob(string? Value) : IJob<string?>;
    public sealed class ResultHandler : IJobHandler<ResultJob, string?>
    {
        public ValueTask<string?> HandleAsync(ResultJob job, CancellationToken cancellationToken) =>
            job.Value switch
            {
                "fail" => throw new InvalidOperationException("failed"),
                "cancel" => throw new OperationCanceledException(),
                "timeout" => throw new TaskCanceledException("The request timed out.", new TimeoutException()),
                _ => ValueTask.FromResult(job.Value)
            };
    }

    public sealed record VoidJob(List<Guid> Ids) : IJob;
    public sealed class ScopeIdentity { public Guid Id { get; } = Guid.NewGuid(); }
    public sealed class VoidHandler(ScopeIdentity identity) : IJobHandler<VoidJob>
    {
        public ValueTask HandleAsync(VoidJob job, CancellationToken cancellationToken)
        {
            job.Ids.Add(identity.Id);
            return ValueTask.CompletedTask;
        }
    }

    public sealed record BlockingJob(TaskCompletionSource Started) : IJob;
    public sealed class BlockingHandler : IJobHandler<BlockingJob>
    {
        public async ValueTask HandleAsync(BlockingJob job, CancellationToken cancellationToken)
        {
            job.Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    public sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

    /// <summary>Every log line the host writes, for asserting on what a failure left behind.</summary>
    public sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyCollection<CapturedLog> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new Writer(categoryName, _entries);

        public async Task<CapturedLog> WaitForAsync(Func<CapturedLog, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_entries.FirstOrDefault(match) is { } entry)
                    return entry;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException("No matching log line was written.");
        }

        public void Dispose()
        {
        }

        private sealed class Writer(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception));
        }
    }
}
