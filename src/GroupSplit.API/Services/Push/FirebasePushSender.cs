using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace GroupSplit.API.Services.Push;

public interface IPushSender
{
    // False means the token is permanently unregistered; other failures are retried.
    Task<bool> SendAsync(string token, Guid groupId, string groupName, Guid deliveryId, CancellationToken ct);
}

public sealed class FirebasePushSender(IConfiguration config, TimeProvider clock) : IPushSender, IDisposable
{
    private FirebaseApp? app;

    public async Task<bool> SendAsync(string token, Guid groupId, string groupName, Guid deliveryId, CancellationToken ct)
    {
        if (!config.GetValue<bool>("Push:Enabled"))
            throw new InvalidOperationException("Push delivery is disabled.");
        app ??= FirebaseApp.Create(new AppOptions
        {
            Credential = await GoogleCredential.GetApplicationDefaultAsync(ct),
            ProjectId = config["Push:ProjectId"]
        }, "groupsplit-push");
        try
        {
            await FirebaseMessaging.GetMessaging(app).SendAsync(
                CreateMessage(token, groupId, groupName, deliveryId, clock.GetUtcNow()), ct);
            return true;
        }
        catch (FirebaseMessagingException error) when (error.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            return false;
        }
    }

    internal static Message CreateMessage(string token, Guid groupId, string groupName,
        Guid deliveryId, DateTimeOffset sentAt) => new()
    {
        Token = token,
        Notification = new Notification { Title = "Group Split", Body = $"An expense was added to {groupName}." },
        Data = new Dictionary<string, string> { ["groupId"] = groupId.ToString() },
        Android = new AndroidConfig
        {
            TimeToLive = TimeSpan.FromHours(1),
            Notification = new AndroidNotification
            {
                ChannelId = "group_expenses", Tag = deliveryId.ToString(),
                // FirebaseAdmin serializes an unset DateTime as year 1. Android then shows
                // an age of thousands of years on notifications it displays in the background.
                EventTimestamp = sentAt.UtcDateTime
            }
        }
    };

    public void Dispose() => app?.Delete();
}
