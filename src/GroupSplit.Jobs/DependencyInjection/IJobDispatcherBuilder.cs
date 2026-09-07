using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobDispatcherBuilder : IJobDispatcherBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>
{
    IJobDispatcherBuilder Use(Func<IServiceProvider, IJobDispatcher> factory);

    IJobDispatcherBuilder IJobDispatcherBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>.Use(IJobDispatcher dispatcher) =>
        Use(_ => dispatcher);
}

public static class JobDispatcherBuilderExtensions
{
    extension(IJobDispatcherBuilder builder)
    {
        public IJobDispatcherBuilder Use<TDispatcher>() where TDispatcher : class, IJobDispatcher
        {
            return builder.Use(ActivatorUtilities.GetServiceOrCreateInstance<TDispatcher>);
        }
    }
}
