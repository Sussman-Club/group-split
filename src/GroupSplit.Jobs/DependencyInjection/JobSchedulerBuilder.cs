using GroupSplit.Jobs.Defaults;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

internal sealed class JobSchedulerBuilder(IJobsBuilder jobsBuilder) : IJobSchedulerBuilder
{
    private Func<IServiceProvider, IJobScheduler>? _factory;
    internal List<(IJob Job, JobSchedule Schedule)> Registrations { get; } = [];
    public IJobsBuilder JobsBuilder => jobsBuilder;

    public IJobSchedulerBuilder Use(Func<IServiceProvider, IJobScheduler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        return this;
    }

    public IJobSchedulerBuilder Add(IJob job, JobSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(schedule);
        Registrations.Add((job, schedule));
        return this;
    }

    IJobScheduler IJobSchedulerBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>
        .BuildScheduler(IServiceProvider services) =>
        (_factory is not null ? _factory(services) : services.GetService<DefaultJobScheduler>())
        ?? throw new InvalidOperationException("No IJobScheduler has been configured.");
}
