using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Firebase.Messaging;
using GroupSplit.App.Shared.Services;

namespace GroupSplit.App;

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class ExpenseMessagingService : FirebaseMessagingService
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
        if (!Microsoft.Maui.Storage.Preferences.Default.Get("push.expenses", false)
            || !message.Data.TryGetValue("groupId", out var group)
            || !Guid.TryParse(group, out var groupId) || groupId == Guid.Empty) return;
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra("groupId", groupId.ToString());
        var id = groupId.GetHashCode();
        var pending = PendingIntent.GetActivity(this, id, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(this, "group_expenses");
        var notification = builder
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle("Group Split")!
            .SetContentText("A new expense was added to your group.")!
            .SetAutoCancel(true)!.SetContentIntent(pending)!.Build();
        NotificationManagerCompat.From(this)?.Notify(id, notification);
    }
}
