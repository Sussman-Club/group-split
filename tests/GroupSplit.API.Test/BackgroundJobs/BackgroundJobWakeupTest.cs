using GroupSplit.API.Extensions;
using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GroupSplit.API.Test.BackgroundJobs;

public class BackgroundJobWakeupTest
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(3, 4, 0)]
    [InlineData(10, 10, 1)]
    [InlineData(25, 10, 1)]
    public async Task Batches_are_bounded_and_a_full_batch_continues_without_waiting_for_a_scan(
        int available, int expectedClaims, int expectedContinuations)
    {
        var calls = 0;
        var processor = new Mock<IBackgroundJobProcessor>();
        processor.Setup(p => p.RunNext(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) <= available);
        var dispatcher = new Mock<IJobDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<IJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IJobHandle>());
        var handler = new ProcessBackgroundJobsHandler(processor.Object, dispatcher.Object,
            NullLogger<ProcessBackgroundJobsHandler>.Instance);

        await handler.HandleAsync(new ProcessBackgroundJobs(), TestContext.Current.CancellationToken);

        Assert.Equal(expectedClaims, calls);
        dispatcher.Verify(d => d.DispatchAsync(It.Is<IJob>(j => j is ProcessBackgroundJobs),
            It.IsAny<CancellationToken>()), Times.Exactly(expectedContinuations));
    }

    [Fact]
    public async Task The_fallback_is_once_a_minute_and_registration_remains_idempotent()
    {
        var services = new ServiceCollection();
        services.AddBackgroundJobs();
        var before = services.ToArray();
        services.AddBackgroundJobs();
        Assert.Equal(before, services.ToArray());
        await using var provider = services.BuildServiceProvider();
        var initializer = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name == "JobScheduleInitializer");
        await initializer.StartAsync(TestContext.Current.CancellationToken);
        var jobs = await provider.GetRequiredService<IJobScheduler>()
            .GetScheduledJobsAsync(TestContext.Current.CancellationToken);
        var scan = Assert.Single(jobs, j => j.Job is ProcessBackgroundJobs);
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<JobSchedule.EverySchedule>(scan.Schedule).Interval);
    }

    [Fact]
    public async Task Cancellation_never_schedules_a_followup_batch()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var dispatcher = new Mock<IJobDispatcher>(MockBehavior.Strict);
        var handler = new ProcessBackgroundJobsHandler(Mock.Of<IBackgroundJobProcessor>(), dispatcher.Object,
            NullLogger<ProcessBackgroundJobsHandler>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new ProcessBackgroundJobs(), cancellation.Token).AsTask());
    }
}
