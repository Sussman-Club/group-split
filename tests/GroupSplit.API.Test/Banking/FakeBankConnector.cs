using GroupSplit.API.Services.Banking;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// A provider that answers with whatever the test scripted, in order, and remembers what it
/// was asked.
/// </summary>
/// <remarks>
/// Everything above the seam is tested through this and never through Plaid: the sync
/// engine's rules are about rows and statuses, and a scripted page says exactly which rows
/// arrived in which order. Each queued answer is either a <see cref="SyncPage"/> or an
/// exception to throw in its place.
/// </remarks>
internal sealed class FakeBankConnector : IBankConnector
{
    public const string Name = "fake";

    public const string AccessToken = "access-fake-token";

    private readonly Queue<object> _answers = new();

    public string Provider => Name;

    /// <summary>The cursor each <see cref="SyncAsync"/> call was made with, in order.</summary>
    public List<string?> CursorsSeen { get; } = [];

    /// <summary>The token each <see cref="SyncAsync"/> call was made with, in order.</summary>
    public List<string> TokensSeen { get; } = [];

    /// <summary>
    /// When set, every sync waits here after being counted, so a test can hold one run open
    /// while it starts another.
    /// </summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Completes the first time a sync is entered.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeBankConnector Answer(SyncPage page)
    {
        _answers.Enqueue(page);
        return this;
    }

    public FakeBankConnector Throw(BankSyncFailure kind)
    {
        _answers.Enqueue(new BankSyncException(kind, $"Scripted {kind}."));
        return this;
    }

    /// <summary>One page, no more to come.</summary>
    public FakeBankConnector Answer(string nextCursor,
        IReadOnlyList<ImportedTransaction>? added = null,
        IReadOnlyList<ImportedTransaction>? modified = null,
        IReadOnlyList<RemovedTransaction>? removed = null,
        bool hasMore = false) =>
        Answer(new SyncPage(added ?? [], modified ?? [], removed ?? [], nextCursor, hasMore));

    public Task<LinkSession> CreateLinkSessionAsync(LinkSessionRequest request, CancellationToken ct = default) =>
        Task.FromResult(new LinkSession("link-fake-token", DateTimeOffset.UtcNow.AddMinutes(30)));

    public Task<LinkedItem> ExchangeAsync(string publicToken, CancellationToken ct = default) =>
        Task.FromResult(new LinkedItem(AccessToken, "item-fake", "Fake Bank",
        [
            new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD")
        ]));

    public async Task<SyncPage> SyncAsync(string accessToken, string? cursor, CancellationToken ct = default)
    {
        TokensSeen.Add(accessToken);
        CursorsSeen.Add(cursor);
        Entered.TrySetResult();

        if (Gate is { } gate)
            await gate.WaitAsync(ct);

        if (_answers.Count == 0)
            throw new InvalidOperationException("The fake connector was asked for a page it was not given.");

        return _answers.Dequeue() switch
        {
            SyncPage page => page,
            Exception exception => throw exception,
            var other => throw new InvalidOperationException($"Unexpected scripted answer {other}.")
        };
    }

    public Task RemoveAsync(string accessToken, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default) =>
        Task.FromResult(headers.TryGetValue("X-Fake-Signature", out var signature) && signature == "valid");

    public WebhookEvent ParseWebhook(string body) => new SyncUpdatesAvailable("item-fake");

    /// <summary>A row as the provider would report it, with only what the test cares about spelled out.</summary>
    public static ImportedTransaction Row(string id, decimal amount, string account = "acc-1",
        bool pending = false, string? replaces = null, string description = "LIDL 1234",
        string? merchant = "Lidl", string? category = "FOOD_AND_DRINK", DateOnly? date = null) =>
        new(account, id, date ?? new DateOnly(2026, 9, 1), amount, "USD", description, merchant, category,
            category is null ? null : category + "_DETAILED", null, "in store", "Lisbon", null,
            pending, replaces, $$$"""{"transaction_id":"{{{id}}}"}""");

    public static RemovedTransaction Removed(string id, string account = "acc-1") => new(account, id);
}
