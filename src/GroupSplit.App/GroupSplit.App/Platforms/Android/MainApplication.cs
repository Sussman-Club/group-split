using Android.App;
using Android.Runtime;
using GroupSplit.Shared;

namespace GroupSplit.App;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            foreach (var kind in PushNotificationKinds.Settings)
                manager?.CreateNotificationChannel(new NotificationChannel(PushNotificationKinds.Channel(kind),
                    PushNotificationKinds.Label(kind), NotificationImportance.Default));
        }
    }
}
