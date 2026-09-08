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
/// <para>
/// Locked, because two threads use it: the test scripts answers and reads what was seen,
/// while the job pump is dequeuing and appending on its own. Unsynchronised, the lists this
/// keeps could be enumerated mid-append -- a flake with nothing to do with what was being
/// tested.
/// </para>
/// </remarks>
internal sealed class FakeBankConnector(string provider = FakeBankConnector.Name) : IBankConnector
{
    public const string Name = "fake";

    public const string AccessToken = "access-fake-token";

    private readonly Lock _gate = new();

    private readonly Queue<object> _answers = new();

    /// <summary>What each exchange answers with, in order. Empty means <see cref="Item"/>.</summary>
    private readonly Queue<LinkedItem> _items = new();

    public string Provider => provider;

    /// <summary>The token each <see cref="RemoveAsync"/> call was made with, in order.</summary>
    public IReadOnlyList<string> RemovedTokens => Snapshot(_removedTokens);

    private readonly List<string> _removedTokens = [];

    /// <summary>
    /// When set, every <see cref="RemoveAsync"/> refuses. Every caller of it here is best
    /// effort, so what a test wants to see is what they do when the provider will not listen.
    /// </summary>
    public bool RefuseRemoval { get; set; }

    /// <summary>The cursor each <see cref="SyncAsync"/> call was made with, in order.</summary>
    public IReadOnlyList<string?> CursorsSeen => Snapshot(_cursorsSeen);

    private readonly List<string?> _cursorsSeen = [];

    /// <summary>The token each <see cref="SyncAsync"/> call was made with, in order.</summary>
    public IReadOnlyList<string> TokensSeen => Snapshot(_tokensSeen);

    private readonly List<string> _tokensSeen = [];

    /// <summary>
    /// The request each <see cref="CreateLinkSessionAsync"/> call was made with, in order.
    /// The webhook address in it is what a real provider would deliver to, so this is where a
    /// test reads the address this deployment hands out.
    /// </summary>
    public IReadOnlyList<LinkSessionRequest> LinkSessionsSeen => Snapshot(_linkSessionsSeen);

    private readonly List<LinkSessionRequest> _linkSessionsSeen = [];

    /// <summary>
    /// When set, every sync waits here after being counted, so a test can hold one run open
    /// while it starts another.
    /// </summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Completes the first time a sync is entered.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeBankConnector Answer(SyncPage page)
    {
        lock (_gate)
            _answers.Enqueue(page);

        return this;
    }

    public FakeBankConnector Throw(BankSyncFailure kind)
    {
        lock (_gate)
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

    public Task<LinkSession> CreateLinkSessionAsync(LinkSessionRequest request, CancellationToken ct = default)
    {
        lock (_gate)
            _linkSessionsSeen.Add(request);

        return Task.FromResult(new LinkSession("link-fake-token", DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    public Task<LinkedItem> ExchangeAsync(string publicToken, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.Count > 0 ? _items.Dequeue() : Item());
    }

    /// <summary>
    /// The item the next exchange answers with. Queued rather than set, because a provider
    /// mints a new item on every link and telling those apart is the point of some tests.
    /// </summary>
    public FakeBankConnector AnswerExchange(LinkedItem item)
    {
        lock (_gate)
            _items.Enqueue(item);

        return this;
    }

    /// <summary>One linked item, with only what the test cares about spelled out.</summary>
    public static LinkedItem Item(
        string itemId = "item-fake",
        string accessToken = AccessToken,
        string institution = "Fake Bank",
        string accountId = "acc-1",
        string name = "Everyday",
        string? mask = "1234",
        string type = "depository") =>
        new(accessToken, itemId, institution,
        [
            new ImportedAccount(accountId, name, mask, type, "checking", "USD")
        ]);

    public async Task<SyncPage> SyncAsync(string accessToken, string? cursor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _tokensSeen.Add(accessToken);
            _cursorsSeen.Add(cursor);
        }

        Entered.TrySetResult();

        if (Gate is { } gate)
            await gate.WaitAsync(ct);

        object answer;

        lock (_gate)
        {
            if (_answers.Count == 0)
                throw new InvalidOperationException("The fake connector was asked for a page it was not given.");

            answer = _answers.Dequeue();
        }

        return answer switch
        {
            SyncPage page => page,
            Exception exception => throw exception,
            var other => throw new InvalidOperationException($"Unexpected scripted answer {other}.")
        };
    }

    public Task RemoveAsync(string accessToken, CancellationToken ct = default)
    {
        // Recorded before the refusal, so a test can tell "would not listen" from "was
        // never asked".
        lock (_gate)
            _removedTokens.Add(accessToken);

        if (RefuseRemoval)
            throw new BankSyncException(BankSyncFailure.Transient, "Scripted refusal to remove.");

        return Task.CompletedTask;
    }

    public Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default) =>
        Task.FromResult(headers.TryGetValue("X-Fake-Signature", out var signature) && signature == "valid");

    /// <summary>
    /// What the next <see cref="ParseWebhook"/> answers with. Scriptable, because the four
    /// notifications do four different things and a fake that only ever says "there are
    /// updates" cannot tell three of them apart.
    /// </summary>
    public Func<string, WebhookEvent> Webhook { get; set; } = _ => new SyncUpdatesAvailable("item-fake");

    public WebhookEvent ParseWebhook(string body) => Webhook(body);

    /// <summary>A row as the provider would report it, with only what the test cares about spelled out.</summary>
    public static ImportedTransaction Row(string id, decimal amount, string account = "acc-1",
        bool pending = false, string? replaces = null, string description = "LIDL 1234",
        string? merchant = "Lidl", string? category = "FOOD_AND_DRINK", DateOnly? date = null) =>
        new(account, id, date ?? new DateOnly(2026, 9, 1), amount, "USD", description, merchant, category,
            category is null ? null : category + "_DETAILED", null, "in store", "Lisbon", null,
            pending, replaces, $$$"""{"transaction_id":"{{{id}}}"}""");

    public static RemovedTransaction Removed(string id, string account = "acc-1") => new(account, id);

    /// <summary>A copy, so a caller can enumerate it while the job pump is still appending.</summary>
    private List<T> Snapshot<T>(List<T> tracked)
    {
        lock (_gate)
            return [.. tracked];
    }
}
