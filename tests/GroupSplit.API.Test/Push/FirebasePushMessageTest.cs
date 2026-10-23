using GroupSplit.API.Services.Push;
using GroupSplit.Shared;
using Newtonsoft.Json.Linq;

namespace GroupSplit.API.Test.Push;

public sealed class FirebasePushMessageTest
{
    [Fact]
    public void Background_notification_serializes_its_actual_UTC_time_instead_of_year_one()
    {
        var sentAt = new DateTimeOffset(2026, 10, 23, 10, 32, 0, TimeSpan.FromHours(-4));
        var message = FirebasePushSender.CreateMessage("test-token", Guid.NewGuid(), "Vacation 2024",
            Guid.NewGuid(), sentAt);

        // Inspect the Firebase wire representation: the SDK serializes event_time even
        // when EventTimestamp was never assigned, which produced the Android age bug.
        var wire = JObject.FromObject(message);
        var timestamp = wire["android"]?["notification"]?["event_time"];
        Assert.NotNull(timestamp);
        Assert.Equal(sentAt.ToUniversalTime(), DateTimeOffset.Parse(timestamp.Value<string>()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("An expense was added to Vacation 2024.", wire["notification"]?["body"]?.Value<string>());
    }
    [Theory]
    [InlineData(PushNotificationKind.ExpenseChanged, "An expense was changed in Trip.")]
    [InlineData(PushNotificationKind.Settlement, "A settlement was recorded in Trip.")]
    [InlineData(PushNotificationKind.PaymentRequest, "You have a payment request in Trip.")]
    [InlineData(PushNotificationKind.InvitationAccepted, "A guest joined Trip.")]
    [InlineData(PushNotificationKind.BankSyncIssue, "A linked bank needs your attention.")]
    public void Categories_include_their_own_channel_and_routing_data(PushNotificationKind kind, string body)
    {
        var groupId = Guid.NewGuid();
        var message = FirebasePushSender.CreateMessage("token", groupId, "Trip", Guid.NewGuid(),
            DateTimeOffset.UtcNow, kind, kind == PushNotificationKind.PaymentRequest ? "request-token" : null);
        Assert.Equal(body, message.Notification.Body);
        Assert.Equal(PushNotificationKinds.Channel(kind), message.Android.Notification.ChannelId);
        Assert.Equal(kind.ToString(), message.Data["notificationKind"]);
    }

    [Fact]
    public void Apple_delivery_uses_the_same_content_and_routing_with_a_bounded_lifetime()
    {
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(1_792_764_000);
        var group = Guid.NewGuid();
        var message = FirebasePushSender.CreateMessage("ios-fcm-token", group, "Trip", Guid.NewGuid(),
            sentAt, PushNotificationKind.PaymentRequest, "request-token");
        var wire = JObject.FromObject(message);
        Assert.Equal("You have a payment request in Trip.", message.Notification.Body);
        Assert.Equal("request-token", message.Data["requestToken"]);
        Assert.Equal("alert", message.Apns.Headers["apns-push-type"]);
        Assert.Equal(sentAt.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            message.Apns.Headers["apns-expiration"]);
        Assert.Equal("default", wire["apns"]?["payload"]?["aps"]?["sound"]?.Value<string>());
        Assert.Equal(group.ToString(), message.Apns.Aps.ThreadId);
    }

    [Fact]
    public void Catalog_covers_every_event_and_keeps_preference_groups_consistent()
    {
        Assert.Equal(Enum.GetValues<PushNotificationKind>().Order(), PushNotificationKinds.Definitions.Select(d => d.Kind).Order());
        Assert.Equal(Enum.GetValues<PushNotificationCategory>().Length, PushNotificationKinds.Settings.Count);
        foreach (var category in PushNotificationKinds.Definitions.GroupBy(d => d.Category))
        {
            Assert.Single(category.Select(d => PushNotificationKinds.Preference(d.Kind)).Distinct());
            Assert.Single(category.Select(d => PushNotificationKinds.Channel(d.Kind)).Distinct());
            foreach (var definition in category)
                Assert.False(string.IsNullOrWhiteSpace(PushNotificationKinds.Body(definition.Kind, "Trip")));
        }
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("../account")]
    [InlineData("token?x=y")]
    public void Payment_notification_routing_rejects_untrusted_paths(string token) =>
        Assert.Null(PushNotificationKinds.Route(PushNotificationKind.PaymentRequest, Guid.NewGuid(), token));

}
