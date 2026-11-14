using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public sealed class BrowserPushNotificationsTest
{
    [Fact]
    public async Task Registration_maps_browser_preferences_to_shared_device_settings()
    {
        var devices = new Mock<IPush_notificationsClient>();
        var push = new BrowserPushNotifications(Mock.Of<IJSRuntime>(), devices.Object);
        var id = Guid.NewGuid();

        await push.RegisterAsync(id, "token", [PushNotificationKinds.Preference(PushNotificationKind.Settlement),
            PushNotificationKinds.Preference(PushNotificationKind.BankSyncIssue)]);

        devices.Verify(client => client.RegisterPushDeviceAsync(id, It.Is<PushDeviceRequest>(request =>
            request.Token == "token" && !request.ExpensesEnabled && request.SettlementsEnabled
            && !request.InvitationsEnabled && request.BankSyncIssuesEnabled && !request.StaffSupportEnabled),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unregister_uses_the_same_device_id()
    {
        var devices = new Mock<IPush_notificationsClient>();
        var push = new BrowserPushNotifications(Mock.Of<IJSRuntime>(), devices.Object);
        var id = Guid.NewGuid();

        await push.UnregisterDeviceAsync(id);

        devices.Verify(client => client.UnregisterPushDeviceAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
