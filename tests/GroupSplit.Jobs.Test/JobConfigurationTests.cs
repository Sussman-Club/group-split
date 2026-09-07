using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GroupSplit.Jobs.Test;

public class JobConfigurationTests
{
    [Fact]
    public void JobsWithMultipleResultContractsCannotBeRegistered()
    {
        var jobs = new ServiceCollection().AddJobs();
        Assert.Contains("multiple result contracts", Assert.Throws<ArgumentException>(
            () => jobs.Handlers.Add<AmbiguousJob, int>(_ => new AmbiguousHandler())).Message);
    }

    [Fact]
    public void ResultJobsCannotUseVoidHandlerRegistration()
    {
        var jobs = new ServiceCollection().AddJobs();
        Assert.Throws<ArgumentException>(
            () => jobs.Handlers.Add<ResultJob>(_ => new DiscardingHandler()));
    }

    private sealed record AmbiguousJob : IJob<int>, IJob<string>;
    private sealed class AmbiguousHandler : IJobHandler<AmbiguousJob, int>
    {
        public ValueTask<int> HandleAsync(AmbiguousJob job, CancellationToken cancellationToken) =>
            ValueTask.FromResult(1);
    }

    private sealed record ResultJob : IJob<int>;
    private sealed class DiscardingHandler : IJobHandler<ResultJob>
    {
        public ValueTask HandleAsync(ResultJob job, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    [Fact]
    public void WithoutDefaultsRequiresExplicitTransportConfiguration()
    {
        var services = new ServiceCollection();
        var jobs = services.AddJobs().WithoutDefaults();
        Assert.Same(jobs, services.AddJobs());
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.Contains(nameof(IJobDispatcher), Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IJobDispatcher>()).Message);
        Assert.Contains(nameof(IJobReceiver), Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IJobReceiver>()).Message);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Namespace == "GroupSplit.Jobs.Defaults");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CustomTransportCanBeConfiguredBeforeOrAfterRemovingDefaults(bool configureFirst)
    {
        var services = new ServiceCollection();
        var jobs = services.AddJobs();
        var dispatcher = new StubDispatcher();
        var receiver = new StubReceiver();
        void Configure()
        {
            jobs.Dispatcher.Use(_ => dispatcher);
            jobs.Receiver.Use(_ => receiver);
        }
        if (configureFirst) Configure();
        jobs.WithoutDefaults();
        if (!configureFirst) Configure();
        using var provider = services.BuildServiceProvider();
        Assert.Same(dispatcher, provider.GetRequiredService<IJobDispatcher>());
        Assert.Same(receiver, provider.GetRequiredService<IJobReceiver>());
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public async Task WithoutDefaultsLeavesReceiverUnresolvedAndPreservesOtherHostedServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<OtherService>();
        var jobs = builder.Services.AddJobs();
        jobs.Receiver.Use(_ => throw new InvalidOperationException("Must not resolve receiver."));
        Assert.Same(jobs, jobs.WithoutDefaults().WithoutDefaults());
        Assert.Same(jobs, builder.Services.AddJobs());

        using var host = builder.Build();
        Assert.IsType<OtherService>(Assert.Single(host.Services.GetServices<IHostedService>()));
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class OtherService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubDispatcher : IJobDispatcher
    {
        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(IJob<TResult> job, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubReceiver : IJobReceiver
    {
        public IAsyncEnumerable<IJobDelivery> ReceiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
