using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.OS;
using GroupSplit.Shared;

namespace GroupSplit.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "https", DataHost = "groupsplit.sussman.win", DataPathPrefix = "/join/", AutoVerify = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "https", DataHost = "groupsplit.sussman.win", DataPathPrefix = "/claim/", AutoVerify = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "groupsplit", DataHost = "invite")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OpenLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenLink(intent);
    }

    private static void OpenLink(Intent? intent)
    {
        var kindText = intent?.GetStringExtra("notificationKind");
        var kind = PushNotificationKind.ExpenseCreated;
        if (kindText is null || (Enum.TryParse(kindText, out kind) && Enum.IsDefined(kind)))
        {
            Guid.TryParse(intent?.GetStringExtra("groupId"), out var groupId);
            IPlatformApplication.Current?.Services.GetRequiredService<Services.NativeLinkRouter>()
                .OpenNotification(kind, groupId, intent?.GetStringExtra("requestToken"));
        }
        if (intent?.Action == Intent.ActionView && Uri.TryCreate(intent.DataString, UriKind.Absolute, out var uri))
            Microsoft.Maui.Controls.Application.Current?.SendOnAppLinkRequestReceived(uri);
    }

    protected override async void OnResume()
    {
        base.OnResume();
        try
        {
            if (IPlatformApplication.Current?.Services.GetService<GroupSplit.App.Shared.Services.IPushNotifications>() is { } push)
                await push.RefreshAsync();
        }
        catch (Exception) { /* Retry token registration when the app next resumes. */ }
    }
}
