using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobsBuilder
    : IJobsBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>
{
    IServiceCollection Services { get; }
}
