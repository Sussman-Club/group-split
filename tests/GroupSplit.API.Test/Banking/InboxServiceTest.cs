using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// What a person does with what a sync brought in. Filing is the one that matters: it is
/// where imported money becomes money the group's balances are made of, so what it copies,
/// what it refuses, and what it links are all pinned here.
/// </summary>
public class InboxServiceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

    [Fact]
    public async Task Filing_into_a_group_copies_the_banks_facts_and_divides_by_the_category()
    {
        var (group, category, other) = await GroupWithEvenCategory();
        var row = await Row(amount: 30m, merchant: "Lidl");

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest
        {
            GroupId = group.Id,
            CategoryId = category
        }, Ct);

        Assert.Equal(30m, expense.Amount);
        Assert.Equal("Lidl", expense.Name);
        Assert.Equal(group.Id, expense.GroupId);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), expense.DateTime.UtcDateTime);

        var splits = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, splits.Count);
        Assert.Equal(30m, splits.Sum(split => split.Amount));
        Assert.Contains(splits, split => split.UserId == other.Id);
    }

    [Fact]
    public async Task Filing_links_the_row_and_the_expense_to_each_other()
    {
        var row = await Row(amount: 12.50m);

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var filed = await Reload(row);
        var linked = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(BankTransactionStatus.Filed, filed.Status);
        Assert.Equal(filed.Id, linked.BankTransactionId);
    }

    [Fact]
    public async Task Filing_with_no_group_is_a_personal_expense_owed_entirely_by_the_payer()
    {
        var row = await Row(amount: 9.99m);

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        Assert.Null(expense.GroupId);

        var split = await DbContext.Set<TransactionSplit>()
            .SingleAsync(candidate => candidate.TransactionId == expense.Id, Ct);

        Assert.Equal(GetService<ICurrentUser>().User.Id, split.UserId);
        Assert.Equal(9.99m, split.Amount);
    }

    [Fact]
    public async Task The_name_falls_back_to_the_banks_own_line_when_there_is_no_merchant()
    {
        var row = await Row(amount: 4m, merchant: null, description: "SQ *COFFEE 4TH ST");

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        Assert.Equal("SQ *COFFEE 4TH ST", expense.Name);
    }

    [Fact]
    public async Task Money_coming_in_cannot_be_filed_as_an_expense()
    {
        var row = await Row(amount: -25m, merchant: "Refund");

        var e = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Inbox.File(row.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.BankTransactionIsCredit, e.Code);
        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
    }

    [Fact]
    public async Task Filing_the_same_row_twice_is_refused()
    {
        var row = await Row(amount: 5m);
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var e = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.File(row.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.BankTransactionAlreadyFiled, e.Code);
        Assert.Equal(1, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    [Fact]
    public async Task A_row_in_another_currency_than_the_group_is_refused_rather_than_converted()
    {
        var (group, category, _) = await GroupWithEvenCategory();
        var row = await Row(amount: 20m, currency: "EUR");

        var e = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.File(row.Id, new FileBankTransactionRequest { GroupId = group.Id, CategoryId = category }, Ct));

        Assert.Equal(ErrorCodes.CurrencyMismatch, e.Code);
        Assert.Equal("EUR", e.Extensions["transactionCurrency"]);
        Assert.Empty(await DbContext.Set<Expense>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Ignoring_takes_a_row_out_of_the_inbox_and_restoring_brings_it_back()
    {
        var row = await Row(amount: 3m);

        await Inbox.Ignore(row.Id, Ct);
        Assert.Equal(BankTransactionStatus.Ignored, (await Reload(row)).Status);
        Assert.Equal(0, (await Inbox.Summary(Ct)).NewCount);

        await Inbox.Restore(row.Id, Ct);
        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
        Assert.Equal(1, (await Inbox.Summary(Ct)).NewCount);
    }

    [Fact]
    public async Task An_expense_cannot_be_ignored_away()
    {
        var row = await Row(amount: 3m);
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var e = await Assert.ThrowsAsync<ConflictException>(() => Inbox.Ignore(row.Id, Ct));

        Assert.Equal(ErrorCodes.BankTransactionAlreadyFiled, e.Code);
    }

    [Fact]
    public async Task The_listing_shows_one_status_at_a_time_and_never_a_superseded_row()
    {
        var waiting = await Row(amount: 1m, providerId: "waiting");
        var ignored = await Row(amount: 2m, providerId: "ignored");
        var superseded = await Row(amount: 3m, providerId: "superseded");

        await Inbox.Ignore(ignored.Id, Ct);
        await SetStatus(superseded, BankTransactionStatus.Superseded);

        var newRows = await (await Inbox.List(new InboxFilter(), Ct)).ToListAsync(Ct);
        var ignoredRows = await (await Inbox.List(new InboxFilter(InboxStatus.Ignored), Ct)).ToListAsync(Ct);

        Assert.Equal([waiting.Id], newRows.Select(row => row.Id));
        Assert.Equal([ignored.Id], ignoredRows.Select(row => row.Id));
        Assert.Equal(1, (await Inbox.Summary(Ct)).NewCount);
    }

    [Fact]
    public async Task Another_persons_row_is_not_found_rather_than_forbidden()
    {
        var row = await Row(amount: 7m);

        using var scope = ServiceProvider.CreateScope();
        await ApiUnitTest.InitializeCurrentUser(scope.ServiceProvider);
        var stranger = scope.ServiceProvider.GetRequiredService<IInboxService>();

        var e = await Assert.ThrowsAsync<NotFoundException>(() =>
            stranger.File(row.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.BankTransactionNotFound, e.Code);
        Assert.Equal(0, (await stranger.Summary(Ct)).NewCount);
    }

    // ---- setup ---------------------------------------------------------------------------

    /// <summary>A group of two with a category that divides evenly, which is the ordinary case.</summary>
    private async Task<(Data.Entities.Group Group, Guid Category, Data.Entities.User Other)> GroupWithEvenCategory()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);
        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group, await CreateEvenCategory(group.Id), other);
    }

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount, string? merchant = "Lidl",
        string description = "LIDL 1234", string currency = "USD", string providerId = "t1")
    {
        var connection = await DbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .FirstOrDefaultAsync(candidate => candidate.UserId == GetService<ICurrentUser>().User.Id, Ct);

        if (connection is null)
        {
            connection = new BankConnection
            {
                User = GetService<ICurrentUser>().User,
                Provider = FakeBankConnector.Name,
                ProviderItemId = $"item-{Guid.NewGuid():N}",
                InstitutionName = "Fake Bank",
                AccessTokenCiphertext = GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
                LinkedAt = DateTimeOffset.UtcNow
            };

            connection.Accounts.Add(new LinkedAccount
            {
                ProviderAccountId = "acc-1",
                Name = "Everyday",
                Type = "depository"
            });

            DbContext.Add(connection);
            await DbContext.SaveChangesAsync(Ct);
        }

        var row = new BankTransaction
        {
            Account = connection.Accounts.First(),
            ProviderTransactionId = providerId,
            Date = new DateOnly(2026, 9, 1),
            Amount = amount,
            Currency = currency,
            Description = description,
            MerchantName = merchant,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }

    private Task<BankTransaction> Reload(BankTransaction row) =>
        DbContext.Set<BankTransaction>().AsNoTracking().SingleAsync(candidate => candidate.Id == row.Id, Ct);

    private async Task SetStatus(BankTransaction row, BankTransactionStatus status)
    {
        var tracked = await DbContext.Set<BankTransaction>().SingleAsync(candidate => candidate.Id == row.Id, Ct);
        tracked.Status = status;
        await DbContext.SaveChangesAsync(Ct);
    }
}
