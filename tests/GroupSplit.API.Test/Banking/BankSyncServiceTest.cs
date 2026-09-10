using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GroupSplit.API.Test.Banking.FakeBankConnector;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The sync engine's rules, one at a time, over a scripted provider. Each is a decision
/// the Phase 3 plan wrote down, and the test is where it stays written down.
/// </summary>
public class BankSyncServiceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(Name, _bank);

    [Fact]
    public async Task A_first_sync_lands_every_added_row_as_new_and_moves_the_cursor()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m), Row("t2", 3.20m, pending: true)]);

        var outcome = await Sync(connection);

        Assert.Equal(SyncOutcome.Completed, outcome);

        var rows = await Rows(connection);
        Assert.Equal(["t1", "t2"], rows.Select(row => row.ProviderTransactionId).Order());
        Assert.All(rows, row => Assert.Equal(BankTransactionStatus.New, row.Status));
        Assert.True(rows.Single(row => row.ProviderTransactionId == "t2").Pending);

        var saved = await Reload(connection);
        Assert.Equal("cursor-1", saved.Cursor);
        Assert.NotNull(saved.LastSyncedAt);
        Assert.Equal([null], _bank.CursorsSeen);
        Assert.Equal([AccessToken], _bank.TokensSeen);
    }

    [Fact]
    public async Task Replaying_the_same_page_changes_nothing()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m)]);
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m)]);

        await Sync(connection);
        var first = (await Rows(connection)).Single();

        await Sync(connection);
        var rows = await Rows(connection);

        var again = Assert.Single(rows);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.ImportedAt, again.ImportedAt);
    }

    [Fact]
    public async Task Pages_are_walked_until_the_provider_has_no_more_and_the_cursor_is_threaded_through()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Answer("cursor-2", added: [Row("t2", 2m)], hasMore: true);
        _bank.Answer("cursor-3", added: [Row("t3", 3m)]);

        await Sync(connection);

        Assert.Equal([null, "cursor-1", "cursor-2"], _bank.CursorsSeen);
        Assert.Equal("cursor-3", (await Reload(connection)).Cursor);
        Assert.Equal(3, (await Rows(connection)).Count);
    }

    [Fact]
    public async Task A_posted_row_supersedes_its_pending_row_and_neither_is_deleted()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("pending-1", 12.50m, pending: true)]);
        _bank.Answer("cursor-2",
            added: [Row("posted-1", 12.50m, replaces: "pending-1")],
            removed: [Removed("pending-1")]);

        await Sync(connection);
        await Sync(connection);

        var rows = await Rows(connection);
        var pending = rows.Single(row => row.ProviderTransactionId == "pending-1");
        var posted = rows.Single(row => row.ProviderTransactionId == "posted-1");

        Assert.Equal(BankTransactionStatus.Superseded, pending.Status);
        Assert.Equal(BankTransactionStatus.New, posted.Status);
        Assert.Equal(pending.Id, posted.ReplacesId);
        Assert.False(posted.Pending);
    }

    [Fact]
    public async Task A_posted_row_takes_over_a_filed_pending_rows_link_to_the_expense()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("pending-1", 12.50m, pending: true)]);
        await Sync(connection);

        var pending = (await Rows(connection)).Single();
        var expense = await FileAsync(pending);

        _bank.Answer("cursor-2", added: [Row("posted-1", 12.50m, replaces: "pending-1")]);
        await Sync(connection);

        var posted = (await Rows(connection)).Single(row => row.ProviderTransactionId == "posted-1");
        var linked = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(BankTransactionStatus.Filed, posted.Status);
        Assert.Equal(posted.Id, linked.BankTransactionId);
        Assert.Equal(12.50m, linked.Amount);
    }

    [Fact]
    public async Task A_posted_row_keeps_an_ignored_pending_row_ignored()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("pending-1", 12.50m, pending: true)]);
        await Sync(connection);

        await SetStatus(connection, "pending-1", BankTransactionStatus.Ignored);

        _bank.Answer("cursor-2", added: [Row("posted-1", 12.50m, replaces: "pending-1")]);
        await Sync(connection);

        var posted = (await Rows(connection)).Single(row => row.ProviderTransactionId == "posted-1");
        Assert.Equal(BankTransactionStatus.Ignored, posted.Status);
    }

    [Theory]
    [InlineData(BankTransactionStatus.New, true, false)]
    [InlineData(BankTransactionStatus.Ignored, true, false)]
    [InlineData(BankTransactionStatus.Filed, false, true)]
    [InlineData(BankTransactionStatus.Superseded, false, false)]
    public async Task A_removal_deletes_a_row_nobody_acted_on_and_stamps_a_filed_one(
        BankTransactionStatus status, bool deleted, bool stamped)
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m)]);
        await Sync(connection);

        if (status == BankTransactionStatus.Filed)
            await FileAsync((await Rows(connection)).Single());
        else
            await SetStatus(connection, "t1", status);

        _bank.Answer("cursor-2", removed: [Removed("t1")]);
        await Sync(connection);

        var rows = await Rows(connection);

        if (deleted)
        {
            Assert.Empty(rows);
            return;
        }

        var row = Assert.Single(rows);
        Assert.Equal(status, row.Status);
        Assert.Equal(stamped, row.RemovedAt is not null);
    }

    [Fact]
    public async Task A_modification_changes_the_row_and_not_the_expense_it_became()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl")]);
        await Sync(connection);

        var row = (await Rows(connection)).Single();
        var expense = await FileAsync(row);

        _bank.Answer("cursor-2", modified: [Row("t1", 13.00m, merchant: "Lidl Express")]);
        await Sync(connection);

        var changed = (await Rows(connection)).Single();
        var kept = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(13.00m, changed.Amount);
        Assert.Equal("Lidl Express", changed.MerchantName);
        Assert.Equal(BankTransactionStatus.Filed, changed.Status);
        Assert.Equal(12.50m, kept.Amount);
        Assert.Equal(changed.Id, kept.BankTransactionId);
    }

    [Fact]
    public async Task An_ignored_row_stays_ignored_across_a_sync_that_modifies_it()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m)]);
        await Sync(connection);
        await SetStatus(connection, "t1", BankTransactionStatus.Ignored);

        _bank.Answer("cursor-2", modified: [Row("t1", 12.99m)]);
        await Sync(connection);

        var row = (await Rows(connection)).Single();
        Assert.Equal(BankTransactionStatus.Ignored, row.Status);
        Assert.Equal(12.99m, row.Amount);
    }

    [Fact]
    public async Task A_modification_to_a_row_never_seen_is_a_row()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", modified: [Row("t1", 12.50m)]);

        await Sync(connection);

        var row = Assert.Single(await Rows(connection));
        Assert.Equal("t1", row.ProviderTransactionId);
        Assert.Equal(BankTransactionStatus.New, row.Status);
    }

    [Fact]
    public async Task Login_required_from_the_provider_marks_the_connection_and_stops()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Throw(BankSyncFailure.LoginRequired);

        var outcome = await Sync(connection);

        Assert.Equal(SyncOutcome.LoginRequired, outcome);
        var saved = await Reload(connection);
        Assert.Equal(BankConnectionStatus.LoginRequired, saved.Status);
        Assert.Null(saved.Cursor);
        Assert.Single(await Rows(connection));

        // And nothing runs against a connection in that state.
        _bank.Answer("cursor-x", added: [Row("t2", 2m)]);
        Assert.Equal(SyncOutcome.NotSyncable, await Sync(connection));
        Assert.Equal(2, _bank.CursorsSeen.Count);
    }

    [Fact]
    public async Task A_mutation_during_pagination_restarts_from_the_cursor_the_run_began_with()
    {
        var connection = await LinkAsync();
        await SetCursor(connection, "cursor-0");

        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Throw(BankSyncFailure.RestartFromCursor);
        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Answer("cursor-2", added: [Row("t2", 2m)]);

        var outcome = await Sync(connection);

        Assert.Equal(SyncOutcome.Completed, outcome);
        Assert.Equal(["cursor-0", "cursor-1", "cursor-0", "cursor-1"], _bank.CursorsSeen);
        Assert.Equal("cursor-2", (await Reload(connection)).Cursor);
        Assert.Equal(2, (await Rows(connection)).Count);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_rows_already_applied_and_leaves_the_cursor()
    {
        var connection = await LinkAsync();
        await SetCursor(connection, "cursor-0");
        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Throw(BankSyncFailure.Transient);

        var outcome = await Sync(connection);

        Assert.Equal(SyncOutcome.Interrupted, outcome);
        Assert.Single(await Rows(connection));
        Assert.Equal("cursor-0", (await Reload(connection)).Cursor);
    }

    [Fact]
    public async Task A_second_sync_while_one_is_running_returns_without_calling_the_provider()
    {
        var connection = await LinkAsync();
        _bank.Gate = new SemaphoreSlim(0, 1);
        _bank.Answer("cursor-1", added: [Row("t1", 1m)]);

        var first = Sync(connection);
        await _bank.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        using var scope = ServiceProvider.CreateScope();
        var second = await scope.ServiceProvider.GetRequiredService<IBankSyncService>().SyncAsync(connection.Id, Ct);

        Assert.Equal(SyncOutcome.AlreadyRunning, second);
        Assert.Single(_bank.CursorsSeen);

        _bank.Gate.Release();
        Assert.Equal(SyncOutcome.Completed, await first);
    }

    /// <summary>
    /// A re-link that lands while a sync is in flight wins: the run that was already going
    /// says nothing about the item it has just stopped being about.
    /// </summary>
    /// <remarks>
    /// Nothing serialises linking against syncing, and a re-link moves the connection onto a
    /// new item -- new id, new token, no cursor -- and retires the old item at the provider.
    /// The run still in flight is holding the retired item's token and, if it were allowed to
    /// finish its writes, would store that item's cursor over the null the re-link just wrote.
    /// The next sync would then send the new token with a cursor from an item the provider
    /// has removed, which it refuses, every time, for ever. Nobody could fix that without
    /// editing a row.
    /// </remarks>
    [Fact]
    public async Task A_sync_that_was_overtaken_by_a_re_link_does_not_store_its_cursor()
    {
        var connection = await LinkAsync();
        await SetCursor(connection, "cursor-0");

        _bank.Gate = new SemaphoreSlim(0, 1);
        _bank.Answer("cursor-1", added: [Row("t1", 1m)]);

        var running = Sync(connection);
        await _bank.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The re-link, as Adopt leaves it, committed while the run above is held open.
        await ReLink(connection, "item-two");

        _bank.Gate.Release();

        Assert.Equal(SyncOutcome.Interrupted, await running);

        var after = await Reload(connection);

        // The re-link's own state stands, untouched by the run it overtook.
        Assert.Null(after.Cursor);
        Assert.True(after.AccountsRekeyed);
        Assert.Equal("item-two", after.ProviderItemId);

        // And what the run did import is kept: it is this account's spending whichever item
        // reported it.
        Assert.Single(await Rows(connection));
    }

    /// <summary>
    /// The other half: a run whose token stops working because the re-link retired the item
    /// underneath it must not mark the freshly linked connection as needing a sign-in.
    /// </summary>
    /// <remarks>
    /// That status is what the card shows and what <c>Sync</c> refuses on, and only a
    /// <c>LOGIN_REPAIRED</c> webhook clears it -- which will never arrive for an item the
    /// provider no longer has. A connection somebody has just successfully re-linked would
    /// sit there asking them to sign in again, with nothing able to answer.
    /// </remarks>
    [Fact]
    public async Task A_sync_that_was_overtaken_by_a_re_link_does_not_mark_it_needing_attention()
    {
        var connection = await LinkAsync();

        _bank.Gate = new SemaphoreSlim(0, 1);
        _bank.Throw(BankSyncFailure.LoginRequired);

        var running = Sync(connection);
        await _bank.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await ReLink(connection, "item-two");

        _bank.Gate.Release();

        Assert.Equal(SyncOutcome.LoginRequired, await running);

        // The outcome is honest about what happened to that token. The connection is not
        // marked, because the token it is about is not this connection's any more.
        Assert.Equal(BankConnectionStatus.Active, (await Reload(connection)).Status);
    }

    /// <summary>
    /// A supersede chain ends on the stronger of the two decisions, whichever end it came
    /// from.
    /// </summary>
    /// <remarks>
    /// The pending row's status used to be copied across, which was safe only while the
    /// posted row was always brand new. A re-key can hand the posted transaction a row this
    /// connection already held, and such a row carries a real decision -- so a pending New,
    /// or a pending Ignored, would demote it. A demoted Filed row lists as unfiled or as
    /// ignored-and-restorable while the expense filed from it still points at it, and
    /// filing it again is a second expense for the same money that nothing warns about.
    /// <para>
    /// Every pair, rather than the two that were found by hand: this is the third time a
    /// status has been quietly downgraded somewhere in the re-key work, and the pairs nobody
    /// thinks of are exactly the ones that got through.
    /// </para>
    /// </remarks>
    [Theory]
    // pending, what the posted row already carries, what the chain must end on
    [InlineData(BankTransactionStatus.New, BankTransactionStatus.New, BankTransactionStatus.New)]
    [InlineData(BankTransactionStatus.New, BankTransactionStatus.Ignored, BankTransactionStatus.Ignored)]
    [InlineData(BankTransactionStatus.New, BankTransactionStatus.Filed, BankTransactionStatus.Filed)]
    [InlineData(BankTransactionStatus.Ignored, BankTransactionStatus.New, BankTransactionStatus.Ignored)]
    [InlineData(BankTransactionStatus.Ignored, BankTransactionStatus.Ignored, BankTransactionStatus.Ignored)]
    [InlineData(BankTransactionStatus.Ignored, BankTransactionStatus.Filed, BankTransactionStatus.Filed)]
    [InlineData(BankTransactionStatus.Filed, BankTransactionStatus.New, BankTransactionStatus.Filed)]
    [InlineData(BankTransactionStatus.Filed, BankTransactionStatus.Ignored, BankTransactionStatus.Filed)]
    [InlineData(BankTransactionStatus.Filed, BankTransactionStatus.Filed, BankTransactionStatus.Filed)]
    public async Task A_supersede_chain_ends_on_the_stronger_decision(
        BankTransactionStatus pending, BankTransactionStatus posted, BankTransactionStatus expected)
    {
        var connection = await LinkAsync();

        // A re-key run, which is the only way the posted transaction can land on a row that
        // already carries a decision.
        await ReKeying(connection);

        // The two rows this connection already holds, identical in everything the re-key
        // matches on, so the replay below claims them.
        await CarriedRowAsync(connection, "old-pending", pending);
        await CarriedRowAsync(connection, "old-posted", posted);

        // The provider replays both under the new item's ids, the second settling the first.
        _bank.Answer("cursor-1", added:
        [
            Row("new-pending", 12.50m, pending: true, description: "LIDL 1234"),
            Row("new-posted", 12.50m, replaces: "new-pending", description: "LIDL 1234")
        ]);

        Assert.Equal(SyncOutcome.Completed, await Sync(connection));

        var rows = await Rows(connection);

        Assert.Equal(expected, rows.Single(row => row.ProviderTransactionId == "new-posted").Status);

        // And the row it took over from says so, whatever it was before.
        Assert.Equal(BankTransactionStatus.Superseded,
            rows.Single(row => row.ProviderTransactionId == "new-pending").Status);
    }

    private async Task ReKeying(BankConnection connection)
    {
        var tracked = await DbContext.Set<BankConnection>().SingleAsync(c => c.Id == connection.Id, Ct);

        tracked.AccountsRekeyed = true;
        tracked.Cursor = null;

        await DbContext.SaveChangesAsync(Ct);
    }

    /// <summary>A row already stored under the previous item's id, ready to be claimed.</summary>
    private async Task CarriedRowAsync(BankConnection connection, string providerId, BankTransactionStatus status)
    {
        var account = await DbContext.Set<LinkedAccount>()
            .SingleAsync(candidate => candidate.BankConnectionId == connection.Id, Ct);

        var row = new BankTransaction
        {
            LinkedAccountId = account.Id,
            ProviderTransactionId = providerId,
            Date = new DateOnly(2026, 9, 1),
            Amount = 12.50m,
            Currency = "USD",
            Description = "LIDL 1234",
            Status = status,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        // A filed row is filed from something, and moving that link is half of what a
        // supersede does.
        if (status is BankTransactionStatus.Filed)
            await FileAsync(row);
    }

    /// <summary>A re-link, as <c>Adopt</c> leaves the row, committed from its own scope.</summary>
    private async Task ReLink(BankConnection connection, string itemId)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

        var tracked = await dbContext.Set<BankConnection>().SingleAsync(c => c.Id == connection.Id, Ct);

        tracked.ProviderItemId = itemId;
        tracked.AccessTokenCiphertext = protector.Protect("token-two");
        tracked.Status = BankConnectionStatus.Active;
        tracked.Cursor = null;
        tracked.AccountsRekeyed = true;

        await dbContext.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_row_on_an_account_the_connection_does_not_know_is_skipped()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 1m), Row("t2", 2m, account: "acc-unknown")]);

        var outcome = await Sync(connection);

        Assert.Equal(SyncOutcome.Completed, outcome);
        var row = Assert.Single(await Rows(connection));
        Assert.Equal("t1", row.ProviderTransactionId);
    }

    [Fact]
    public async Task The_provider_string_is_kept_out_of_the_columns_and_clipped_to_them()
    {
        var connection = await LinkAsync();
        var longLine = new string('x', 400);
        _bank.Answer("cursor-1", added: [Row("t1", 1m, description: longLine)]);

        await Sync(connection);

        var row = (await Rows(connection)).Single();
        Assert.Equal(256, row.Description.Length);
        Assert.Contains("\"t1\"", row.RawJson);
    }

    // ---- setup ---------------------------------------------------------------------------

    /// <summary>
    /// A connection the current user made through the fake provider, with one account,
    /// exactly as the exchange endpoint will write it.
    /// </summary>
    /// <summary>
    /// The bug this whole area exists for. Somebody shares a second account through update
    /// mode, which mints no new item and so runs nothing on the linking path -- and every
    /// row of that account used to be dropped with a log line, for the life of the
    /// connection, while the card said the bank was healthy and last checked minutes ago.
    /// </summary>
    [Fact]
    public async Task A_row_on_an_account_shared_after_linking_is_imported_rather_than_dropped()
    {
        var connection = await LinkAsync();

        // What the provider says now: the account it was linked with, and the one shared
        // since. Nothing has told this connection about the second one.
        _bank.AnswerAccounts(
        [
            new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD"),
            new ImportedAccount("acc-2", "Savings", "5678", "depository", "savings", "USD")
        ]);

        _bank.Answer("cursor-1", added: [Row("t1", 12.50m), Row("t2", 8m, account: "acc-2")]);

        Assert.Equal(SyncOutcome.Completed, await Sync(connection));

        var rows = await Rows(connection);

        Assert.Equal(["t1", "t2"], rows.Select(row => row.ProviderTransactionId).Order());

        var saved = await ReloadWithAccounts(connection);

        Assert.Contains(saved.Accounts, account => account.ProviderAccountId == "acc-2");

        // Resolved, so there is nothing for the person to do and the card says nothing.
        Assert.False(saved.AccountsNotShared);
    }

    /// <summary>
    /// The provider is asked once however many pages meet an account it did not report, and
    /// not at all when every account is known. Cheap, but not free.
    /// </summary>
    [Fact]
    public async Task The_account_list_is_read_at_most_once_a_run_and_only_when_a_page_needs_it()
    {
        var connection = await LinkAsync();

        _bank.Answer("cursor-1", added: [Row("t1", 1m)], hasMore: true);
        _bank.Answer("cursor-2", added: [Row("t2", 2m)]);

        await Sync(connection);

        Assert.Equal(0, _bank.AccountReads);

        // Two pages, both naming an account the provider will not admit to. Still one ask.
        _bank.Answer("cursor-3", added: [Row("t3", 3m, account: "ghost")], hasMore: true);
        _bank.Answer("cursor-4", added: [Row("t4", 4m, account: "ghost")]);

        await Sync(connection);

        Assert.Equal(1, _bank.AccountReads);
    }

    /// <summary>
    /// Asking did not help: the provider does not report the account either, because the
    /// person has not shared it. That is the one case where the rows really are dropped --
    /// and the connection is flagged, so somebody other than the log knows.
    /// </summary>
    [Fact]
    public async Task A_row_on_an_account_the_provider_will_not_share_flags_the_connection()
    {
        var connection = await LinkAsync();

        _bank.AnswerAccounts([new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD")]);
        _bank.Answer("cursor-1", added: [Row("mine", 12.50m), Row("theirs", 8m, account: "acc-2")]);

        Assert.Equal(SyncOutcome.Completed, await Sync(connection));

        // The rows on accounts it does know are unaffected: one account nobody shared must
        // not stop the rest of the connection importing.
        Assert.Equal(["mine"], (await Rows(connection)).Select(row => row.ProviderTransactionId));

        Assert.True((await Reload(connection)).AccountsNotShared);
    }

    /// <summary>
    /// A provider that cannot answer must not take the run down with it. The accounts
    /// already known keep importing, and the connection ends up flagged exactly as it would
    /// have if the answer had come back without the account in it.
    /// </summary>
    [Fact]
    public async Task A_provider_that_cannot_list_its_accounts_does_not_stop_the_rows_that_are_fine()
    {
        var connection = await LinkAsync();

        _bank.RefuseAccountsWith = BankSyncFailure.Transient;
        _bank.Answer("cursor-1", added: [Row("mine", 12.50m), Row("theirs", 8m, account: "acc-2")]);

        Assert.Equal(SyncOutcome.Completed, await Sync(connection));

        Assert.Equal(["mine"], (await Rows(connection)).Select(row => row.ProviderTransactionId));
        Assert.True((await Reload(connection)).AccountsNotShared);
    }

    private Task<BankConnection> ReloadWithAccounts(BankConnection connection) =>
        DbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == connection.Id, Ct);

    private async Task<BankConnection> LinkAsync()
    {
        var protector = GetService<IAccessTokenProtector>();
        var user = GetService<ICurrentUser>().User;

        var connection = new BankConnection
        {
            User = user,
            Provider = Name,
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = protector.Protect(AccessToken),
            LinkedAt = DateTimeOffset.UtcNow
        };

        connection.Accounts.Add(new LinkedAccount
        {
            ProviderAccountId = "acc-1",
            Name = "Everyday",
            Mask = "1234",
            Type = "depository",
            Subtype = "checking"
        });

        DbContext.Add(connection);
        await DbContext.SaveChangesAsync(Ct);

        return connection;
    }

    private Task<SyncOutcome> Sync(BankConnection connection) =>
        GetService<IBankSyncService>().SyncAsync(connection.Id, Ct);

    private Task<List<BankTransaction>> Rows(BankConnection connection) =>
        DbContext.Set<BankTransaction>()
            .AsNoTracking()
            .Where(row => row.Account.BankConnectionId == connection.Id)
            .ToListAsync(Ct);

    private Task<BankConnection> Reload(BankConnection connection) =>
        DbContext.Set<BankConnection>().AsNoTracking().SingleAsync(c => c.Id == connection.Id, Ct);

    private async Task SetStatus(BankConnection connection, string providerId, BankTransactionStatus status)
    {
        var row = await DbContext.Set<BankTransaction>()
            .SingleAsync(r => r.Account.BankConnectionId == connection.Id && r.ProviderTransactionId == providerId, Ct);
        row.Status = status;
        await DbContext.SaveChangesAsync(Ct);
    }

    private async Task SetCursor(BankConnection connection, string cursor)
    {
        var tracked = await DbContext.Set<BankConnection>().SingleAsync(c => c.Id == connection.Id, Ct);
        tracked.Cursor = cursor;
        await DbContext.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// What filing will do once the inbox exists: an expense built from the row, linked back
    /// to it, and the row marked. Spelled out here so the sync rules about filed rows can be
    /// tested before the inbox is.
    /// </summary>
    private async Task<Expense> FileAsync(BankTransaction row)
    {
        var user = GetService<ICurrentUser>().User;
        var tracked = await DbContext.Set<BankTransaction>().SingleAsync(r => r.Id == row.Id, Ct);

        var expense = new Expense
        {
            User = user,
            Amount = tracked.Amount,
            Currency = tracked.Currency,
            DateTime = tracked.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Name = tracked.MerchantName ?? tracked.Description,
            BankTransaction = tracked
        };
        expense.Splits.Add(new TransactionSplit { User = user, Amount = tracked.Amount });

        tracked.Status = BankTransactionStatus.Filed;

        DbContext.Add(expense);
        await DbContext.SaveChangesAsync(Ct);

        return expense;
    }
}
