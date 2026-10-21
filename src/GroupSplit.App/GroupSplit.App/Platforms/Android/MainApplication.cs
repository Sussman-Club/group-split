using Android.App;
using Android.Runtime;

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
            manager?.CreateNotificationChannel(new NotificationChannel("group_expenses", "Expense alerts", NotificationImportance.Default));
        }
    }
}
