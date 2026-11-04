using System.Security.Cryptography;
using System.Text.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

/// <summary>Retains recent receipt suggestions while navigating within the current session.</summary>
public sealed class ReceiptSuggestionCache : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly DataChangeNotifier _changes;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private long _generation;

    public ReceiptSuggestionCache(DataChangeNotifier changes)
    {
        _changes = changes;
        changes.GroupsChanged += Invalidate;
        changes.TransactionsChanged += Invalidate;
    }

    public long Generation { get { lock (_entries) return _generation; } }

    public FilingDestinationSuggestion? Get(ReceiptInboxResponse receipt)
    {
        lock (_entries)
        {
        if (!_entries.TryGetValue(receipt.Id, out var entry)) return null;
        if (entry.Expires <= DateTimeOffset.UtcNow || entry.Fingerprint != Fingerprint(receipt))
        {
            _entries.Remove(receipt.Id);
            return null;
        }
        return entry.Suggestion;
        }
    }

    public void Remember(ReceiptInboxResponse receipt, FilingDestinationSuggestion suggestion, long generation)
    {
        lock (_entries)
        {
        if (generation != _generation) return;
        _entries[receipt.Id] = new(Fingerprint(receipt), suggestion, DateTimeOffset.UtcNow.Add(Lifetime));
        }
    }

    public void Retain(IReadOnlyList<ReceiptInboxResponse> receipts)
    {
        var ids = receipts.Select(receipt => receipt.Id).ToHashSet();
        lock (_entries)
            foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToArray()) _entries.Remove(id);
    }

    private Task Invalidate()
    {
        lock (_entries) { _generation++; _entries.Clear(); }
        return Task.CompletedTask;
    }

    private static string Fingerprint(ReceiptInboxResponse row)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            row.Currency, row.Receipt.MerchantName, row.Receipt.PurchaseDate, row.Receipt.Total,
            Items = row.Receipt.Items.Select(item => item.Name).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public void Dispose()
    {
        _changes.GroupsChanged -= Invalidate;
        _changes.TransactionsChanged -= Invalidate;
    }

    private sealed record Entry(string Fingerprint, FilingDestinationSuggestion Suggestion, DateTimeOffset Expires);
}
