#if ANDROID
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Android.Gms.Extensions;
using Firebase.Messaging;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

public sealed class AndroidPushNotifications(NativeSession session, NativeOptions options,
    IHttpClientFactory clients) : IPushNotifications
{
    private const string InstallationKey = "push.installation";
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Supported => Firebase.FirebaseApp.InitializeApp(Android.App.Application.Context) is not null;
    public bool Enabled => Enum.GetValues<PushNotificationKind>().Any(IsEnabled);
    public bool IsEnabled(PushNotificationKind kind) => Preferences.Default.Get(PushNotificationKinds.Preference(kind), false);

    public Task SetEnabledAsync(bool enabled) => SetEnabledAsync(PushNotificationKind.ExpenseCreated, enabled);

    public async Task SetEnabledAsync(PushNotificationKind kind, bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            if (!Supported) throw new InvalidOperationException("Firebase is not configured.");
            if (enabled && OperatingSystem.IsAndroidVersionAtLeast(33)
                && await Permissions.RequestAsync<NotificationPermission>() != PermissionStatus.Granted)
                throw new InvalidOperationException("Notification permission was not granted.");
            if (enabled && AndroidX.Core.App.NotificationManagerCompat.From(Android.App.Application.Context)?.AreNotificationsEnabled() != true)
                throw new InvalidOperationException("Notifications are disabled in Android settings.");
            var key = PushNotificationKinds.Preference(kind);
            var previous = IsEnabled(kind);
            Preferences.Default.Set(key, enabled);
            try
            {
                FirebaseMessaging.Instance.AutoInitEnabled = Enabled;
                if (Enabled) await RegisterAsync();
                else await RemoveAsync();
            }
            catch
            {
                Preferences.Default.Set(key, previous);
                FirebaseMessaging.Instance.AutoInitEnabled = Enabled;
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RefreshAsync(Guid? userId = null)
    {
        if (!Supported) return;
        await gate.WaitAsync();
        try
        {
            if (userId is { } owner)
            {
                var previous = Preferences.Default.Get("push.owner", "");
                if (previous.Length != 0 && previous != owner.ToString()) await RemoveAsync();
                Preferences.Default.Set("push.owner", owner.ToString());
            }
            if (Enabled) await RegisterAsync();
        }
        finally { gate.Release(); }
    }

    public async Task UnregisterAsync(bool accountDeleted = false)
    {
        await gate.WaitAsync();
        try { await RemoveAsync(accountDeleted); }
        finally { gate.Release(); }
    }

    private async Task RegisterAsync()
    {
        var access = await session.AccessTokenAsync();
        if (access is null) throw new InvalidOperationException("Sign in to enable alerts.");
        var result = await FirebaseMessaging.Instance.GetToken();
        var token = result?.ToString() ?? throw new InvalidOperationException("No push token.");
        var id = Preferences.Default.Get(InstallationKey, "");
        if (!Guid.TryParse(id, out _)) { id = Guid.NewGuid().ToString(); Preferences.Default.Set(InstallationKey, id); }
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(options.ApiBaseAddress, $"users/me/push-devices/{id}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content = JsonContent.Create(new PushDeviceRequest(token,
            IsEnabled(PushNotificationKind.ExpenseCreated), IsEnabled(PushNotificationKind.ExpenseChanged),
            IsEnabled(PushNotificationKind.Settlement), IsEnabled(PushNotificationKind.PaymentRequest),
            IsEnabled(PushNotificationKind.InvitationAccepted), IsEnabled(PushNotificationKind.BankSyncIssue)), NativeJsonContext.Default.PushDeviceRequest);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task RemoveAsync(bool skipServer = false)
    {
        foreach (var kind in Enum.GetValues<PushNotificationKind>())
            Preferences.Default.Set(PushNotificationKinds.Preference(kind), false);
        Exception? failure = null;
        try
        {
            var access = skipServer ? null : await session.AccessTokenAsync();
            var id = Preferences.Default.Get(InstallationKey, "");
            if (access is not null && Guid.TryParse(id, out _))
            {
                using var client = clients.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(options.ApiBaseAddress, $"users/me/push-devices/{id}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            Preferences.Default.Remove(InstallationKey);
            try
            {
                if (Supported)
                {
                    FirebaseMessaging.Instance.AutoInitEnabled = false;
                    await FirebaseMessaging.Instance.DeleteToken();
                }
            }
            finally { AndroidX.Core.App.NotificationManagerCompat.From(Android.App.Application.Context)?.CancelAll(); }
        }
        if (failure is not null) throw new InvalidOperationException("Server unregister failed.", failure);
    }

    public sealed class NotificationPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [("android.permission.POST_NOTIFICATIONS", true)];
    }
}
#endif
