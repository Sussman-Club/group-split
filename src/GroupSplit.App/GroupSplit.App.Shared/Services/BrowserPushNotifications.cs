using GroupSplit.Shared;
using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Services;

public sealed class BrowserPushNotifications(IJSRuntime js, IPush_notificationsClient devices) : IPushNotifications, IDisposable
{
    private DotNetObjectReference<BrowserPushNotifications>? callback;
    private IJSObjectReference? module;
    private BrowserPushState state = new(false, null, []);
    public bool Supported => state.Supported;
    public bool Enabled => state.Preferences.Length != 0;
    public string DeviceDescription => "Choose alerts for this browser on this device. Your other browsers and Android devices have separate settings.";
    public string? UnavailableReason => state.Reason;
    public bool IsEnabled(PushNotificationKind kind) => state.Preferences.Contains(PushNotificationKinds.Preference(kind));
    public Task SetEnabledAsync(bool enabled) => SetEnabledAsync(PushNotificationKind.ExpenseCreated, enabled);

    private async Task<IJSObjectReference> Module() => module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/GroupSplit.App.Shared/js/browser-push.js");
    public async Task InitializeAsync()
    {
        callback ??= DotNetObjectReference.Create(this);
        state = await (await Module()).InvokeAsync<BrowserPushState>("initialize", callback);
    }
    public async Task SetEnabledAsync(PushNotificationKind kind, bool enabled) =>
        state = await (await Module()).InvokeAsync<BrowserPushState>("setEnabled", PushNotificationKinds.Preference(kind), enabled, callback ??= DotNetObjectReference.Create(this));
    public async Task RefreshAsync(Guid? userId = null) =>
        state = await (await Module()).InvokeAsync<BrowserPushState>("refresh", userId?.ToString(), callback ??= DotNetObjectReference.Create(this));
    public async Task UnregisterAsync(bool accountDeleted = false) =>
        state = await (await Module()).InvokeAsync<BrowserPushState>("unregister", accountDeleted, callback ??= DotNetObjectReference.Create(this));

    [JSInvokable]
    public Task RegisterAsync(Guid id, string token, string[] preferences)
    {
        bool Has(PushNotificationKind kind) => preferences.Contains(PushNotificationKinds.Preference(kind));
        return devices.RegisterPushDeviceAsync(id, new PushDeviceRequest(token,
            Has(PushNotificationKind.ExpenseCreated), Has(PushNotificationKind.Settlement),
            Has(PushNotificationKind.InvitationAccepted), Has(PushNotificationKind.BankSyncIssue),
            Has(PushNotificationKind.StaffTicketCreated)));
    }

    [JSInvokable]
    public Task UnregisterDeviceAsync(Guid id) => devices.UnregisterPushDeviceAsync(id);

    public void Dispose() => callback?.Dispose();

    public sealed record BrowserPushState(bool Supported, string? Reason, string[] Preferences);
}
