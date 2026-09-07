using Microsoft.Extensions.DependencyInjection;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Jobs.Standalone;

/// <summary>Configures one internally hosted jobs system using caller-owned instances.</summary>
public sealed class JobsBuilder
    : IJobsBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder, SchedulerBuilder>
{
    private readonly IServiceCollection _services = new ServiceCollection();
    private bool _built;
    internal DependencyInjection.IJobsBuilder Inner { get; }
    public DispatcherBuilder Dispatcher { get; }
    public HandlersBuilder Handlers { get; }
    public ReceiverBuilder Receiver { get; }
    public SchedulerBuilder Scheduler { get; }

    public JobsBuilder()
    {
        Inner = _services.AddJobs();
        Dispatcher = new(this);
        Handlers = new(this);
        Receiver = new(this);
        Scheduler = new(this);
    }

    public JobsBuilder WithoutDefaults()
    {
        EnsureMutable();
        Inner.WithoutDefaults();
        return this;
    }

    public JobsRuntime Build() => BuildAsync().GetAwaiter().GetResult();

    public async Task<JobsRuntime> BuildAsync(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                foreach (var descriptor in _services)
                    services.Add(descriptor);
            })
            .Build();

        try
        {
            var dispatcher = host.Services.GetRequiredService<IJobDispatcher>();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            _built = true;
            return new JobsRuntime(host, dispatcher);
        }
        catch
        {
            if (host is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
            else
                host.Dispose();
            throw;
        }
    }

    internal void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("This builder has already built a runtime.");
    }
}
