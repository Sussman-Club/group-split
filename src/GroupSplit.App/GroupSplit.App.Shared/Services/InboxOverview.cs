using GroupSplit.Shared;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Errors;
namespace GroupSplit.App.Shared.Services;

public sealed class InboxOverview(IReceiptsClient receipts, IInboxStateService bank, ApiErrorPresenter errors,
    DataChangeNotifier changes) : IDisposable
{
    public int? ReceiptCount
    {
        get; private set;
    }
    public int BankCount => bank.NewCount;
    public int? Total => ReceiptCount is { } count ? count + BankCount : null;
    public event Action? Changed;
    private Task? _load;
    private bool _subscribed;
    public Task EnsureLoaded()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            bank.OnChanged += Notify;
            changes.ReceiptsChanged += Refresh;
        }
        return _load ??= Refresh();
    }
    public async Task Refresh()
    {
        await bank.EnsureLoadedAsync();
        await errors.TryAsync(async () => ReceiptCount = (await receipts.ListReceiptInboxAsync()).Count, "Could not load receipt count.");
        Notify();
    }
    public void SetReceiptCount(int count)
    {
        ReceiptCount = count;
        Notify();
    }
    private void Notify() => Changed?.Invoke();
    public void Dispose()
    {
        bank.OnChanged -= Notify;
        changes.ReceiptsChanged -= Refresh;
    }
}
