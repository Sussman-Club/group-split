using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.Push;

public interface IPushSender
{
    // False means the token is permanently unregistered; other failures are retried.
    Task<bool> SendAsync(string token, Guid groupId, string groupName, Guid deliveryId, CancellationToken ct,
        PushNotificationKind kind = PushNotificationKind.ExpenseCreated, string? requestToken = null);
}

public sealed class FirebasePushSender(IConfiguration config, TimeProvider clock) : IPushSender, IDisposable
{
    private FirebaseApp? app;

    public async Task<bool> SendAsync(string token, Guid groupId, string groupName, Guid deliveryId, CancellationToken ct,
        PushNotificationKind kind = PushNotificationKind.ExpenseCreated, string? requestToken = null)
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
                CreateMessage(token, groupId, groupName, deliveryId, clock.GetUtcNow(), kind, requestToken), ct);
            return true;
        }
        catch (FirebaseMessagingException error) when (error.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            return false;
        }
    }

    internal static Message CreateMessage(string token, Guid groupId, string groupName,
        Guid deliveryId, DateTimeOffset sentAt,
        PushNotificationKind kind = PushNotificationKind.ExpenseCreated, string? requestToken = null) => new()
    {
        Token = token,
        Notification = new Notification { Title = "Group Split", Body = Body(kind, groupName) },
        Data = Data(kind, groupId, requestToken),
        Android = new AndroidConfig
        {
            TimeToLive = TimeSpan.FromHours(1),
            Notification = new AndroidNotification
            {
                ChannelId = PushNotificationKinds.Channel(kind), Tag = deliveryId.ToString(),
                // FirebaseAdmin serializes an unset DateTime as year 1. Android then shows
                // an age of thousands of years on notifications it displays in the background.
                EventTimestamp = sentAt.UtcDateTime
            }
        }
    };

    private static string Body(PushNotificationKind kind, string name) => kind switch
    {
        PushNotificationKind.ExpenseCreated => $"An expense was added to {name}.",
        PushNotificationKind.ExpenseChanged => $"An expense was changed in {name}.",
        PushNotificationKind.Settlement => $"A settlement was recorded in {name}.",
        PushNotificationKind.PaymentRequest => $"You have a payment request in {name}.",
        PushNotificationKind.InvitationAccepted => $"A guest joined {name}.",
        PushNotificationKind.BankSyncIssue => "A linked bank needs your attention.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Dictionary<string, string> Data(PushNotificationKind kind, Guid groupId, string? requestToken)
    {
        var data = new Dictionary<string, string> { ["notificationKind"] = kind.ToString() };
        if (groupId != Guid.Empty) data["groupId"] = groupId.ToString();
        if (requestToken is not null) data["requestToken"] = requestToken;
        return data;
    }

    public void Dispose() => app?.Delete();
}
