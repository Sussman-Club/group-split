using GroupSplit.Jobs.Defaults;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public class JobReceiverBuilder(IJobsBuilder jobs) : IJobReceiverBuilder
{
    private Func<IServiceProvider, IJobReceiver>? _factory;

    public IJobsBuilder Jobs { get; } = jobs;

    public IJobReceiverBuilder Use(Func<IServiceProvider, IJobReceiver> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        return this;
    }

    IJobReceiver IJobReceiverBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>.BuildReceiver(
        IServiceProvider serviceProvider) =>
        (_factory is not null
            ? _factory(serviceProvider)
            : serviceProvider.GetService<DefaultReceiver>())
        ?? throw new InvalidOperationException("No IJobReceiver has been configured.");
}
