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
            Credential = await CredentialAsync(config, ct),
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

    internal static Task<GoogleCredential> CredentialAsync(IConfiguration config, CancellationToken ct)
    {
        var encoded = config["Push:ServiceAccountBase64"];
        if (string.IsNullOrWhiteSpace(encoded)) return GoogleCredential.GetApplicationDefaultAsync(ct);
        // Select the credential type explicitly: deployment configuration must never load
        // arbitrary external-account credential definitions or write a private key to disk.
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return Task.FromResult(GoogleCredential.FromServiceAccountCredential(
                CredentialFactory.FromJson<ServiceAccountCredential>(json)));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or System.Text.Json.JsonException
                                      or Newtonsoft.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("The Firebase service-account configuration is invalid.");
        }
    }

    internal static Message CreateMessage(string token, Guid groupId, string groupName,
        Guid deliveryId, DateTimeOffset sentAt,
        PushNotificationKind kind = PushNotificationKind.ExpenseCreated, string? requestToken = null) => new()
    {
        Token = token,
        Notification = new Notification { Title = "Group Split", Body = PushNotificationKinds.Body(kind, groupName) },
        Data = Data(kind, groupId, requestToken),
        Apns = new ApnsConfig
        {
            Headers = new Dictionary<string, string>
            {
                ["apns-push-type"] = "alert", ["apns-priority"] = "10",
                ["apns-expiration"] = sentAt.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            Aps = new Aps { Sound = "default", ThreadId = groupId == Guid.Empty ? "account" : groupId.ToString() }
        },
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

    private static Dictionary<string, string> Data(PushNotificationKind kind, Guid groupId, string? requestToken)
    {
        var data = new Dictionary<string, string> { ["notificationKind"] = kind.ToString() };
        if (groupId != Guid.Empty) data["groupId"] = groupId.ToString();
        if (requestToken is not null) data["requestToken"] = requestToken;
        return data;
    }

    public void Dispose() => app?.Delete();
}
