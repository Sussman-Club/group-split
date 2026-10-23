using GroupSplit.API.Services.Push;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.API.Test.Push;

public sealed class PushDeliveryTest : IAsyncLifetime
{
    private ApiEndpointHost host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync() => host = await ApiEndpointHost.StartAsync();
    public ValueTask DisposeAsync() => host.DisposeAsync();

    [Theory]
    [InlineData("sent", 1, 1)]
    [InlineData("unregistered", 1, 0)]
    [InlineData("retry", 1, 1)]
    [InlineData("disabled", 0, 1)]
    [InlineData("departed", 0, 1)]
    [InlineData("expired", 0, 1)]
    [InlineData("stale", 0, 1)]
    [InlineData("deleted", 0, 1)]
    [InlineData("reassigned", 0, 1)]
    public async Task Pending_deliveries_check_current_access_and_handle_provider_results(
        string scenario, int sends, int deviceCount)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new Data.Entities.User { FirstName = "Recipient" };
        var group = new Data.Entities.Group { Name = "Trip" };
        if (scenario != "departed") group.Users.Add(user);
        db.AddRange(user, group);
        var device = new PushDevice { UserId = user.Id, Token = "token", ExpensesEnabled = scenario != "disabled",
            UpdatedAt = DateTimeOffset.UtcNow };
        if (scenario == "stale") device.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-31);
        if (scenario == "deleted") user.Kind = UserKind.Deleted;
        var delivery = new SendExpensePush(device.Id,
            scenario == "reassigned" ? Guid.NewGuid() : user.Id, group.Id, Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(scenario == "expired" ? -1 : 1));
        db.Add(device);
        await db.SaveChangesAsync(Ct);
        var sender = new Mock<IPushSender>();
        var setup = sender.Setup(s => s.SendAsync("token", group.Id, delivery.DeliveryId, It.IsAny<CancellationToken>()));
        if (scenario == "retry") setup.ThrowsAsync(new HttpRequestException("Unavailable"));
        else setup.ReturnsAsync(scenario != "unregistered");
        var handler = new SendExpensePushHandler(db, sender.Object, TimeProvider.System);
        if (scenario == "retry")
            await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(delivery, Ct).AsTask());
        else
            await handler.HandleAsync(delivery, Ct);
        sender.Verify(s => s.SendAsync("token", group.Id, delivery.DeliveryId, It.IsAny<CancellationToken>()), Times.Exactly(sends));
        db.ChangeTracker.Clear();
        Assert.Equal(deviceCount, await db.Set<PushDevice>().CountAsync(Ct));
    }
}
