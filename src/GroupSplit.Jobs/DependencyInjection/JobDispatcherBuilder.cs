using GroupSplit.Jobs.Defaults;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

internal class JobDispatcherBuilder(IJobsBuilder jobsBuilder) : IJobDispatcherBuilder
{
    private Func<IServiceProvider, IJobDispatcher>? _factory;

    public IJobsBuilder JobsBuilder => jobsBuilder;

    public IJobDispatcherBuilder Use(Func<IServiceProvider, IJobDispatcher> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        return this;
    }

    IJobDispatcher IJobDispatcherBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>.BuildDispatcher(
        IServiceProvider serviceProvider) =>
        (_factory is not null
            ? _factory(serviceProvider)
            : serviceProvider.GetService<DefaultDispatcher>())
        ?? throw new InvalidOperationException("No IJobDispatcher has been configured.");
}
