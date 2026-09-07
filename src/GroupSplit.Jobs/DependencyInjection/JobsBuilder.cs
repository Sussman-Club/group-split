using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

internal class JobsBuilder : IJobsBuilder
{
    public IServiceCollection Services { get; }

    public IJobHandlersBuilder Handlers { get; }
    public IJobDispatcherBuilder Dispatcher { get; }
    public IJobReceiverBuilder Receiver { get; }

    public JobsBuilder(IServiceCollection services)
    {
        Services = services;
        Handlers = new JobHandlersBuilder(this);
        Dispatcher = new JobDispatcherBuilder(this);
        Receiver = new JobReceiverBuilder(this);
    }
}
