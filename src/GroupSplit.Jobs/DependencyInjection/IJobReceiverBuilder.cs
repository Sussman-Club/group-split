using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobReceiverBuilder : IJobReceiverBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>
{
    IJobReceiverBuilder Use(Func<IServiceProvider, IJobReceiver> factory);

    IJobReceiverBuilder IJobReceiverBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>.Use(IJobReceiver receiver) =>
        Use(_ => receiver);
}

public static class JobReceiverBuilderExtensions
{
    extension(IJobReceiverBuilder builder)
    {
        public IJobReceiverBuilder Use<TReceiver>() where TReceiver : class, IJobReceiver
        {
            return builder.Use(ActivatorUtilities.GetServiceOrCreateInstance<TReceiver>);
        }
    }
}
