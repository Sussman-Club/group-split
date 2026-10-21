using GroupSplit.API.Services.Push;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GroupSplit.API.Test.Push;

public sealed class PushDeliveryTest : IAsyncLifetime
{
    private ApiEndpointHost host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync() => host = await ApiEndpointHost.StartAsync();
    public ValueTask DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("sent", 1, 0, 1)]
    [InlineData("unregistered", 1, 0, 0)]
    [InlineData("retry", 1, 1, 1)]
    [InlineData("disabled", 0, 0, 1)]
    [InlineData("departed", 0, 0, 1)]
    [InlineData("expired", 0, 0, 1)]
    [InlineData("exhausted", 0, 0, 1)]
    public async Task Pending_deliveries_check_current_access_and_handle_provider_results(
        string scenario, int sends, int pendingCount, int deviceCount)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new Data.Entities.User { FirstName = "Recipient" };
        var group = new Data.Entities.Group { Name = "Trip" };
        if (scenario != "departed") group.Users.Add(user);
        db.AddRange(user, group);
        var device = new PushDevice { UserId = user.Id, Token = "token", ExpensesEnabled = scenario != "disabled",
            UpdatedAt = DateTimeOffset.UtcNow };
        var delivery = new PushDelivery { UserId = user.Id, DeviceId = device.Id, GroupId = group.Id,
            NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(scenario == "expired" ? -1 : 1),
            Attempts = scenario == "exhausted" ? 5 : 0 };
        db.AddRange(device, delivery);
        await db.SaveChangesAsync(Ct);
        var sender = new Mock<IPushSender>();
        var setup = sender.Setup(s => s.SendAsync("token", group.Id, delivery.Id, It.IsAny<CancellationToken>()));
        if (scenario == "retry") setup.ThrowsAsync(new HttpRequestException("Unavailable"));
        else setup.ReturnsAsync(scenario != "unregistered");
        using var worker = new FirebasePushWorker(host.Services.GetRequiredService<IServiceScopeFactory>(), sender.Object,
            NullLogger<FirebasePushWorker>.Instance);
        await worker.Deliver(Ct);
        sender.Verify(s => s.SendAsync("token", group.Id, delivery.Id, It.IsAny<CancellationToken>()), Times.Exactly(sends));
        db.ChangeTracker.Clear();
        Assert.Equal(pendingCount, await db.Set<PushDelivery>().CountAsync(Ct));
        Assert.Equal(deviceCount, await db.Set<PushDevice>().CountAsync(Ct));
        if (pendingCount > 0)
        {
            var pending = await db.Set<PushDelivery>().SingleAsync(Ct);
            Assert.Equal(1, pending.Attempts);
            Assert.True(pending.NextAttemptAt > DateTimeOffset.UtcNow);
        }
    }
}
