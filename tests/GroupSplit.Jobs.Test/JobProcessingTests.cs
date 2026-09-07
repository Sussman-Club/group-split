using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    private static IHost CreateHost()
    {
        var host = Host.CreateApplicationBuilder();
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
}
