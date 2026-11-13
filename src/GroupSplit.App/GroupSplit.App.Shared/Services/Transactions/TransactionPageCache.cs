using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>A bounded cache owned by one ledger filter generation, never shared across users.</summary>
public sealed class TransactionPageCache<T>
{
    private const int Capacity = 8;
    private readonly Dictionary<(int Page, int Size), PagedResponse<T>> _pages = [];
    private readonly Queue<(int Page, int Size)> _order = [];
    private readonly object _sync = new();

    public async Task<PagedResponse<T>> GetAsync(int page, int size, CancellationToken ct,
        Func<int, int, CancellationToken, Task<PagedResponse<T>>> fetch)
    {
        ct.ThrowIfCancellationRequested();
        var key = (page, size);
        lock (_sync)
        {
            if (_pages.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var result = await fetch(page, size, ct);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_pages.ContainsKey(key))
            {
                if (_pages.Count == Capacity)
                {
                    _pages.Remove(_order.Dequeue());
                }

                _pages.Add(key, result);
                _order.Enqueue(key);
            }
        }
        return result;
    }
}
