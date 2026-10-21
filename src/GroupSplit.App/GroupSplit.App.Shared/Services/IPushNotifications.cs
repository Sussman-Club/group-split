namespace GroupSplit.App.Shared.Services;

public interface IPushNotifications
{
    bool Supported { get; }
    bool Enabled { get; }
    Task SetEnabledAsync(bool enabled);
    Task RefreshAsync(Guid? userId = null);
    Task UnregisterAsync(bool accountDeleted = false);
}
