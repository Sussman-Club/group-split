using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

public interface IPushNotifications
{
    bool Supported { get; }
    bool Enabled { get; }
    Task SetEnabledAsync(bool enabled);
    bool IsEnabled(PushNotificationKind kind) => kind == PushNotificationKind.ExpenseCreated && Enabled;
    Task SetEnabledAsync(PushNotificationKind kind, bool enabled) => kind == PushNotificationKind.ExpenseCreated
        ? SetEnabledAsync(enabled) : throw new NotSupportedException();
    Task RefreshAsync(Guid? userId = null);
    Task UnregisterAsync(bool accountDeleted = false);
}
