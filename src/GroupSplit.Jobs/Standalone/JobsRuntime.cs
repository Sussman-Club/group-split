using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Jobs.Standalone;

/// <summary>Owns the internal host, but not caller-supplied handlers or transports.</summary>
public sealed class JobsRuntime : IJobDispatcher, IAsyncDisposable
{
    private readonly IHost _host;
    private readonly IJobDispatcher _dispatcher;
    private bool _disposed;

    public Task Completion { get; }

    internal JobsRuntime(IHost host, IJobDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        Completion = Task.WhenAll(host.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>().Select(service => service.ExecuteTask ?? Task.CompletedTask));
    }

    public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.DispatchAsync(job, cancellationToken);
    }

    public Task<IJobHandle<TResult>> DispatchAsync<TResult>(
        IJob<TResult> job, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.DispatchAsync(job, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_host is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
            else
                _host.Dispose();
        }
    }
}
