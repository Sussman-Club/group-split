using GroupSplit.Jobs.Standalone;
using Xunit;

namespace GroupSplit.Jobs.Test;

public class StandaloneTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancellingAWaitDoesNotCancelExecutionOrOtherWaiters()
    {
        var builder = new JobsBuilder();
        var handler = new GatedHandler();
        builder.Handlers.Add(handler);
        await using var runtime = await builder.BuildAsync(Token);
        var handle = await runtime.DispatchAsync(new GatedRequest(), Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelledWait = handle.GetResultAsync(cancellation.Token).AsTask();
        var otherWait = handle.GetResultAsync(Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.False(otherWait.IsCompleted);
        handler.Release.TrySetResult(42);
        Assert.Equal(42, await otherWait.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(42, await handle.GetResultAsync(Token));
    }

    [Fact]
    public async Task MissingHandlerFailsItsHandleAndWorkerContinues()
    {
        var builder = new JobsBuilder();
        builder.Handlers.Add<Request, string?>(new Handler());
        await using var runtime = await builder.BuildAsync(Token);
        var missing = await runtime.DispatchAsync(new Notify(), Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => missing.WaitAsync(Token).AsTask());
        Assert.Contains("No handler registered", error.Message);
        var next = await runtime.DispatchAsync(new Request("still running"), Token);
        Assert.Equal("still running", await next.GetResultAsync(Token));
    }

    [Fact]
    public void RegistrationRejectsDuplicatesAndInvalidResultContracts()
    {
        var builder = new JobsBuilder();
        builder.Handlers.Add<Notify>(new Handler());
        Assert.Throws<ArgumentException>(() => builder.Handlers.Add<Notify>(new Handler()));
        Assert.Throws<ArgumentException>(() => builder.Handlers.Add<Request>(new DiscardingHandler()));
        Assert.Throws<ArgumentException>(() => builder.Handlers.Add<AmbiguousRequest, int>(new AmbiguousHandler()));
    }

    [Fact]
    public async Task ConfigurationIsFrozenAfterBuild()
    {
        var builder = new JobsBuilder();
        var dispatcher = builder.Dispatcher;
        var receiver = builder.Receiver;
        var handlers = builder.Handlers;
        await using var runtime = await builder.BuildAsync(Token);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Use(new Dispatcher()));
        Assert.Throws<InvalidOperationException>(() => receiver.Use(new UnusedReceiver()));
        Assert.Throws<InvalidOperationException>(() => handlers.Add<Notify>(new Handler()));
        Assert.Throws<InvalidOperationException>(() => builder.WithoutDefaults());
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
    }

    [Fact]
    public async Task CustomReceiverIsConsumedAndRemainsCallerOwned()
    {
        var receiver = new SingleDeliveryReceiver();
        var builder = new JobsBuilder();
        builder.Receiver.Use(receiver);
        builder.Handlers.Add<Request, string?>(new Handler());
        await using var runtime = await builder.BuildAsync(Token);
        Assert.Equal("custom", await receiver.Delivery.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await runtime.DisposeAsync();
        Assert.True(receiver.Stopped);
        Assert.False(receiver.Disposed);
    }

    public sealed record GatedRequest : IJob<int>;
    private sealed class GatedHandler : IJobHandler<GatedRequest, int>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> HandleAsync(GatedRequest job, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed record AmbiguousRequest : IJob<int>, IJob<string>;
    private sealed class AmbiguousHandler : IJobHandler<AmbiguousRequest, int>
    {
        public ValueTask<int> HandleAsync(AmbiguousRequest job, CancellationToken cancellationToken) =>
            ValueTask.FromResult(1);
    }
    private sealed class DiscardingHandler : IJobHandler<Request>
    {
        public ValueTask HandleAsync(Request job, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class SingleDeliveryReceiver : IJobReceiver, IDisposable
    {
        public TestDelivery Delivery { get; } = new();
        public bool Stopped;
        public bool Disposed;
        public async IAsyncEnumerable<IJobDelivery> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                yield return Delivery;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { Stopped = true; }
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestDelivery : IJobDelivery
    {
        public IJob Job { get; } = new Request("custom");
        public TaskCompletionSource<object?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask CompleteAsync(object? result, CancellationToken cancellationToken = default)
        {
            Result.TrySetResult(result);
            return ValueTask.CompletedTask;
        }
        public ValueTask FailAsync(Exception exception, CancellationToken cancellationToken = default)
        {
            Result.TrySetException(exception);
            return ValueTask.CompletedTask;
        }
        public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        {
            Result.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task BuiltInstancesHandleResultsVoidJobsAndFailures()
    {
        var builder = new JobsBuilder();
        var handler = new Handler();
        builder.Handlers.Add<Request, string?>(handler);
        builder.Handlers.Add<Notify>(handler);
        await using var runtime = await builder.BuildAsync(Token);
        var result = await runtime.DispatchAsync(new Request("hello"), Token);
        Assert.Equal("hello", await result.GetResultAsync(Token));
        Assert.Equal("hello", await result.GetResultAsync(Token));
        await result.WaitAsync(Token);
        Assert.Null(await (await runtime.DispatchAsync(new Request(null), Token)).GetResultAsync(Token));
        await (await runtime.DispatchAsync(new Notify(), Token)).WaitAsync(Token);
        Assert.Equal(1, handler.Notifications);
        var failed = await runtime.DispatchAsync(new Request("fail"), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.GetResultAsync(Token).AsTask());
        await runtime.DisposeAsync();
        Assert.False(handler.Disposed);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Handlers.Add<Notify>(handler));
    }

    [Fact]
    public async Task DisposalCancelsRunningAndPendingJobsAndPreservesCallerOwnership()
    {
        var builder = new JobsBuilder();
        var handler = new BlockingHandler();
        builder.Handlers.Add<Block>(handler);
        var runtime = builder.Build();
        var running = await runtime.DispatchAsync(new Block(), Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var pending = await runtime.DispatchAsync(new Block(), Token);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Token).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.DispatchAsync(new Block(), Token));
    }

    [Fact]
    public async Task WithoutDefaultsUsesCustomDispatcherWithoutStartingReceiver()
    {
        var builder = new JobsBuilder().WithoutDefaults();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        var dispatcher = new Dispatcher();
        builder.Dispatcher.Use(dispatcher);
        builder.Receiver.Use(new UnusedReceiver());
        await using var runtime = await builder.BuildAsync(Token);
        await (await runtime.DispatchAsync(new Notify(), Token)).WaitAsync(Token);
        Assert.Equal(1, dispatcher.Calls);
        Assert.True(runtime.Completion.IsCompletedSuccessfully);
        await runtime.DisposeAsync();
        Assert.False(dispatcher.Disposed);
    }

    public sealed record Request(string? Value) : IJob<string?>;
    public sealed record Notify : IJob;
    private sealed class Handler : IJobHandler<Request, string?>, IJobHandler<Notify>, IDisposable
    {
        public int Notifications;
        public bool Disposed;
        public ValueTask<string?> HandleAsync(Request job, CancellationToken cancellationToken) =>
            job.Value == "fail" ? throw new InvalidOperationException("failed") : ValueTask.FromResult(job.Value);
        public ValueTask HandleAsync(Notify job, CancellationToken cancellationToken)
        {
            Notifications++;
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    public sealed record Block : IJob;
    private sealed class BlockingHandler : IJobHandler<Block>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HandleAsync(Block job, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class Dispatcher : IJobDispatcher, IDisposable
    {
        public int Calls;
        public bool Disposed;
        public void Dispose() => Disposed = true;
        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IJobHandle>(new GroupSplit.Jobs.Defaults.DefaultJobHandle(Task.CompletedTask));
        }
        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(IJob<TResult> job, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed class UnusedReceiver : IJobReceiver
    {
        public IAsyncEnumerable<IJobDelivery> ReceiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must not run.");
    }
}
