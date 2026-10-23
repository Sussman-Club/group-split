using GroupSplit.API.Services.Push;
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
}
