using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Firebase.Messaging;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App;

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class PushMessagingService : FirebaseMessagingService
{
    public override async void OnNewToken(string token)
    {
        base.OnNewToken(token);
        try
        {
            if (IPlatformApplication.Current?.Services.GetService<IPushNotifications>() is { } push)
                await push.RefreshAsync();
        }
        catch (Exception) { /* The next app resume retries registration. */ }
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        var kind = PushNotificationKind.ExpenseCreated;
        if (message.Data.TryGetValue("notificationKind", out var kindText)
            && (!Enum.TryParse(kindText, out kind) || !Enum.IsDefined(kind))) return;
        if (!Microsoft.Maui.Storage.Preferences.Default.Get(PushNotificationKinds.Preference(kind), false)) return;
        message.Data.TryGetValue("groupId", out var group);
        Guid.TryParse(group, out var groupId);
        message.Data.TryGetValue("requestToken", out var requestToken);
        if (PushNotificationKinds.Route(kind, groupId, requestToken) is null) return;
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra("groupId", groupId.ToString());
        intent.PutExtra("notificationKind", kind.ToString());
        if (requestToken is not null) intent.PutExtra("requestToken", requestToken);
        var id = HashCode.Combine(kind, groupId, requestToken);
        var pending = PendingIntent.GetActivity(this, id, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(this, PushNotificationKinds.Channel(kind));
        var notification = builder
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle("Group Split")!
            .SetContentText(message.GetNotification()?.Body ?? PushNotificationKinds.Body(kind, "your group"))!
            .SetAutoCancel(true)!.SetContentIntent(pending)!.Build();
        NotificationManagerCompat.From(this)?.Notify(id, notification);
    }
}
