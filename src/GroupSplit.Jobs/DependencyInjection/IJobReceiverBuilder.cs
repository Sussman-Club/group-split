using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobReceiverBuilder : IJobReceiverBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>
{
    IJobReceiverBuilder Use(Func<IServiceProvider, IJobReceiver> factory);

    IJobReceiverBuilder IJobReceiverBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>.Use(IJobReceiver receiver) =>
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
