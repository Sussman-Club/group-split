using System.Net.Http.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// Linking a bank that is already linked, which is what somebody does when a connection
/// misbehaves and repairing it is not offered.
/// </summary>
/// <remarks>
/// The match on the provider's item id cannot see this: a provider mints a new item on every
/// link, so the second one shares no id with the first. What it shares is the bank and the
/// accounts behind it, and that is what has to be recognised -- or the person collects a
/// duplicate of a bank they already had, every row arrives twice, and an item is spent at
/// the provider for nothing. On a plan whose item allowance is spent once and never
/// returned, that last part does not come back.
/// </remarks>
public class DuplicateLinkTest : IAsyncLifetime
{
    private readonly FakeBankConnector _bank = new();

    private ApiEndpointHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await ApiEndpointHost.StartAsync(services =>
        {
            services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
            services.Configure<BankingOptions>(options => options.Provider = FakeBankConnector.Name);
        });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task Link()
    {
        var response = await _host.Client.PostAsJsonAsync(
            "/bank-connections", new CreateBankConnectionRequest { PublicToken = "public-token" }, Ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task<List<BankTransaction>> Rows()
    {
        await using var scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankTransaction>()
            .AsNoTracking()
            .ToListAsync(Ct);
    }

    /// <summary>Files a row into the caller's own spending, so it carries a decision.</summary>
    private async Task FileAsync(Guid rowId)
    {
        var response = await _host.Client.PostAsJsonAsync(
            $"/inbox/{rowId}/file", new FileBankTransactionRequest(), Ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Waits until <paramref name="settled"/> holds, or fails saying it never did.
    /// </summary>
    /// <remarks>
    /// Waiting on what the work produced, not on the connector being entered. Counting calls
    /// into the fake was the obvious thing and it is wrong by exactly one step: the count is
    /// taken at the top of SyncAsync, before the page it is about to answer with has been
    /// applied or saved -- so a wait on it can return with nothing written, and the
    /// assertions that follow read an empty database. It passed in a full run only because
    /// the other tests kept the machine busy enough for the poll to sleep at least once.
    /// </remarks>
    private static async Task Eventually(Func<Task<bool>> settled, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await settled())
                return;

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Waits for a sync run to have finished and stored where it got to.</summary>
    private Task SyncedTo(string cursor) =>
        Eventually(async () => (await Connections()).Any(connection => connection.Cursor == cursor),
            $"a sync run to complete at {cursor}");

    /// <summary>Asks for a sync. What to wait for afterwards is the caller's business.</summary>
    private async Task SyncAsync()
    {
        var connection = (await Connections()).Single();

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/sync", null, Ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task<List<BankConnection>> Connections()
    {
        await using var scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task Linking_the_same_bank_again_moves_the_connection_it_already_had()
    {
        // A page each, so the sync every link asks for has something to read.
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();
        await Link();

        var connection = Assert.Single(await Connections());

        Assert.Equal("item-two", connection.ProviderItemId);

        // One real account behind two items. Matching on the provider's account id would
        // have added a second row here and orphaned the rows filed against the first.
        Assert.Single(connection.Accounts);
        Assert.Equal("acc-1", connection.Accounts.Single().ProviderAccountId);
    }

    /// <summary>
    /// Otherwise it is left running at the provider, counted, with nothing on this side
    /// holding a token that could ever name it again.
    /// </summary>
    [Fact]
    public async Task The_item_that_was_replaced_is_retired_at_the_provider()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();
        await Link();

        Assert.Contains("token-one", _bank.RemovedTokens);
        Assert.DoesNotContain("token-two", _bank.RemovedTokens);
    }

    /// <summary>
    /// The cursor is the replaced item's place in its own stream, and resuming a new item
    /// from it would ask the provider about somewhere it has never been.
    /// </summary>
    [Fact]
    public async Task The_replaced_items_cursor_is_not_carried_over()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();

        // The first item's sync has to have finished and stored its cursor before the
        // re-link, or there is nothing for the adoption to clear and the test proves
        // nothing. Worse, the run left in flight then reads the connection after the
        // adoption -- so it is a sync of the new item, and the cursor it stores is the new
        // item's own. The second run resuming from that is correct, and indistinguishable
        // from the bug below to a fake whose pages do not say which item they came from.
        await SyncedTo("cursor-one");

        await Link();

        // Waited for, or the assertion below reads a list with one entry in it and passes
        // without the adoption's sync having started.
        await SyncedTo("cursor-two");

        // Whatever the sync that follows the adoption has since written, the cursor the
        // first item left behind must not be what the new one started from.
        Assert.DoesNotContain("cursor-one", _bank.CursorsSeen.Skip(1));
    }

    /// <summary>
    /// Two genuinely separate logins at one bank -- a personal one and a business one --
    /// have no account in common, and are the second connection the person asked for.
    /// </summary>
    [Fact]
    public async Task Two_logins_at_one_bank_stay_two_connections()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item(
                "item-two", "token-two", accountId: "acc-9", name: "Business", mask: "9876"));

        await Link();
        await Link();

        Assert.Equal(2, (await Connections()).Count);
        Assert.Empty(_bank.RemovedTokens);
    }

    /// <summary>
    /// Adopting a re-link does not re-import the history the connection already holds, even
    /// though the provider reports every row of it again under ids nothing here has seen.
    /// </summary>
    /// <remarks>
    /// This is the sting in the tail of adoption. The cursor has to be cleared -- it is the
    /// replaced item's place in its own stream -- so the next sync reads the history from the
    /// beginning; and a provider mints transaction ids per item, so not one of those rows is
    /// recognised by the id it arrives with. Left alone, every already-filed expense of the
    /// last however-many months comes back as a fresh inbox row, and the duplicate check
    /// cannot warn about a single one: it only compares against expenses that came from no
    /// bank row, and these all came from one.
    /// </remarks>
    [Fact]
    public async Task Adopting_a_re_link_does_not_import_the_history_it_already_has()
    {
        var spend = FakeBankConnector.Row("old-1", 12.50m, description: "LIDL 1234");

        // The first login, and a page that brings in one row.
        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"));
        _bank.Answer("cursor-one", added: [spend]);

        await Link();
        await SyncedTo("cursor-one");

        var before = Assert.Single(await Rows());
        Assert.Equal("old-1", before.ProviderTransactionId);

        // File it, so the row is not merely present but carries a decision that has to
        // survive: this is what a duplicate would be a duplicate of.
        await FileAsync(before.Id);

        // The re-link. Same bank, same mask, new item -- and the provider replays the same
        // spending under an id minted for the new item.
        _bank.AnswerExchange(FakeBankConnector.Item("item-two", "token-two", accountId: "acc-9"));
        _bank.Answer("cursor-two", added: [FakeBankConnector.Row("new-1", 12.50m, account: "acc-9",
            description: "LIDL 1234")]);

        await Link();
        await SyncedTo("cursor-two");

        // One row, not two: the one that was already here, now answering to the new id and
        // still filed.
        var after = Assert.Single(await Rows());

        Assert.Equal(before.Id, after.Id);
        Assert.Equal("new-1", after.ProviderTransactionId);
        Assert.Equal(BankTransactionStatus.Filed, after.Status);

        // And the licence to match on appearance is spent: from here on two identical
        // amounts on one day are two transactions, because they are.
        await using var scope = _host.Services.CreateAsyncScope();

        Assert.False((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>().AsNoTracking().SingleAsync(Ct)).AccountsRekeyed);
    }

    /// <summary>
    /// A re-key run that is interrupted and picks up again does not hand one of two
    /// identical-looking rows to the wrong transaction.
    /// </summary>
    /// <remarks>
    /// The subtle one, and the reason re-keying tracks what it has already spoken for rather
    /// than simply counting down a queue. Two spends of the same amount on the same day at
    /// the same shop are one key with two rows behind it. The first run re-keys one of them
    /// and then fails partway; the second run replays from the same cursor and meets that
    /// row again -- by the new id this time, so the ordinary index finds it. If it were still
    /// sitting in the carried-over history as unclaimed, the run's other transaction could
    /// claim it: the first transaction would then belong to nothing, disappear from the
    /// inbox for good once the cursor moved past it, and any expense filed from it would be
    /// pointing at the wrong row.
    /// </remarks>
    [Fact]
    public async Task A_re_key_run_that_resumes_does_not_hand_one_row_to_two_transactions()
    {
        // Two spends a person really made, identical in everything the match can see.
        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"));
        _bank.Answer("cursor-one", added:
        [
            FakeBankConnector.Row("old-1", 12.50m, description: "LIDL 1234"),
            FakeBankConnector.Row("old-2", 12.50m, description: "LIDL 1234")
        ]);

        await Link();
        await SyncedTo("cursor-one");

        Assert.Equal(2, (await Rows()).Count);

        // The re-link, and a first catch-up run that reads one page and then breaks. Both
        // transactions come back under the new item's ids, one page each.
        _bank.AnswerExchange(FakeBankConnector.Item("item-two", "token-two", accountId: "acc-9"));

        _bank.Answer(new SyncPage(
            [FakeBankConnector.Row("new-1", 12.50m, account: "acc-9", description: "LIDL 1234")],
            [], [], "cursor-two", HasMore: true));

        _bank.Throw(BankSyncFailure.Transient);

        await Link();

        // The interrupted run stores no cursor, so what says it happened is the row its one
        // good page re-keyed.
        await Eventually(async () => (await Rows()).Any(row => row.ProviderTransactionId == "new-1"),
            "the interrupted run to have re-keyed its first page");

        // The run that picks it up: the same two pages from the same cursor, this time both.
        _bank.Answer(new SyncPage(
            [FakeBankConnector.Row("new-1", 12.50m, account: "acc-9", description: "LIDL 1234")],
            [], [], "cursor-two", HasMore: true));

        _bank.Answer("cursor-three", added:
        [
            FakeBankConnector.Row("new-2", 12.50m, account: "acc-9", description: "LIDL 1234")
        ]);

        await SyncAsync();
        await SyncedTo("cursor-three");

        var rows = await Rows();

        // Still two rows -- neither duplicated nor lost -- and each transaction has one of
        // its own.
        Assert.Equal(2, rows.Count);
        Assert.Equal(["new-1", "new-2"], rows.Select(row => row.ProviderTransactionId).Order());
    }

    /// <summary>
    /// A superseded row is never what a replayed transaction attaches to.
    /// </summary>
    /// <remarks>
    /// A pending row and the posted row that took over from it are identical in date, amount
    /// and description, so with superseded rows in the running a replay can land on one.
    /// That puts the live transaction on a row the inbox filters out -- invisible -- and if
    /// the row that carries the decision is still around it keeps an id the provider has
    /// forgotten, so every later change to that transaction lands on the wrong row.
    /// <para>
    /// Seeded directly rather than played out through a supersede chain, so which row gets
    /// claimed is not a matter of which one the database happened to return first: here
    /// there is exactly one candidate and it is the one that must not be chosen.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_superseded_row_is_never_what_a_replayed_transaction_attaches_to()
    {
        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"));
        _bank.Answer("cursor-one");

        await Link();
        await SyncedTo("cursor-one");

        var superseded = await SupersededRowAsync(12.50m, "LIDL 1234");

        // The re-link, replaying a transaction that looks exactly like it.
        _bank.AnswerExchange(FakeBankConnector.Item("item-two", "token-two", accountId: "acc-9"));
        _bank.Answer("cursor-two", added:
        [
            FakeBankConnector.Row("new-posted", 12.50m, account: "acc-9", description: "LIDL 1234")
        ]);

        await Link();
        await SyncedTo("cursor-two");

        var rows = await Rows();

        // The superseded row is untouched, and the transaction got a row of its own.
        Assert.Equal("old-pending", rows.Single(row => row.Id == superseded).ProviderTransactionId);
        Assert.Contains(rows, row => row.ProviderTransactionId == "new-posted" && row.Id != superseded);
    }

    /// <summary>One row left behind by a supersede chain, and nothing else.</summary>
    private async Task<Guid> SupersededRowAsync(decimal amount, string description)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var account = await dbContext.Set<LinkedAccount>().FirstAsync(Ct);

        var row = new BankTransaction
        {
            LinkedAccountId = account.Id,
            ProviderTransactionId = "old-pending",
            Date = new DateOnly(2026, 9, 1),
            Amount = amount,
            Currency = "USD",
            Description = description,
            Status = BankTransactionStatus.Superseded,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        dbContext.Add(row);
        await dbContext.SaveChangesAsync(Ct);

        return row.Id;
    }

    /// <summary>
    /// Two separate logins at one bank that reports no masks, both showing an account called
    /// the obvious thing. They are not the same bank connection and must not be treated as
    /// one.
    /// </summary>
    /// <remarks>
    /// The name is the only thing left to compare here and it is not evidence: "Checking" is
    /// what half the accounts at any bank are called. Matching on it would adopt -- move the
    /// first connection onto the second login's item, re-point its accounts at accounts they
    /// are not, and remove the first item at the provider -- and the person would lose a
    /// working connection and find their history filed against the wrong account. So a
    /// maskless account matches nothing, and the cost of that is the thing this whole file
    /// is about: one spent item, which is the cheaper of the two mistakes by a long way.
    /// </remarks>
    [Fact]
    public async Task Two_maskless_logins_at_one_bank_stay_two_connections()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item(
                "item-one", "token-one", accountId: "acc-1", name: "Checking", mask: null))
            .AnswerExchange(FakeBankConnector.Item(
                "item-two", "token-two", accountId: "acc-2", name: "Checking", mask: null));

        await Link();
        await Link();

        Assert.Equal(2, (await Connections()).Count);

        // And nothing was retired: the first login is still linked and still works.
        Assert.Empty(_bank.RemovedTokens);
    }

    /// <summary>
    /// Linking the same item a second time, when the bank has an account it did not report
    /// the first time.
    /// </summary>
    /// <remarks>
    /// The other half of the account-list problem, and the one the old code told people to
    /// use: "picking up a new account is a re-link, not a sync". It was not, because the
    /// merge here reached a stored connection through its navigation, and
    /// <see cref="GroupSplit.Data.Entities.Entity"/> hands every instance an Id at
    /// construction -- so EF read the new account as one it already had, staged an UPDATE
    /// and failed the save. What the person saw was the link being "held safely" and
    /// nothing needing doing, for ever, while the account never arrived.
    /// </remarks>
    [Fact]
    public async Task Linking_the_same_item_again_picks_up_an_account_it_did_not_have()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(new LinkedItem("token-one", "item-one", "Fake Bank",
            [
                new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD"),
                new ImportedAccount("acc-2", "Savings", "5678", "depository", "savings", "USD")
            ]));

        await Link();
        await Link();

        var connection = Assert.Single(await Connections());

        Assert.Equal(["acc-1", "acc-2"], connection.Accounts.Select(a => a.ProviderAccountId).Order());
    }
}
