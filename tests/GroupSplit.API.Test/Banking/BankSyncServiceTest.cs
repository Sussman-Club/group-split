using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
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
