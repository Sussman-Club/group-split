using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GroupSplit.API.Test.Banking.FakeBankConnector;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The merchant table: one row per place, and the path a logo takes from the provider to
/// an expense in somebody's ledger.
/// </summary>
/// <remarks>
/// The point of the table is that it is shared, so most of what is worth pinning here is
/// about not making a second row: forty payments at one shop, the same shop under a
/// different casing, a logo that only turns up on the fortieth of them.
/// </remarks>
public class MerchantTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(Name, _bank);

    [Fact]
    public async Task Many_rows_at_one_shop_make_one_merchant_and_all_point_at_it()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added:
        [
            Row("t1", 12.50m, merchant: "Lidl"),
            Row("t2", 8.20m, merchant: "Lidl"),
            Row("t3", 31.00m, merchant: "Lidl")
        ]);

        await Sync(connection);

        var merchant = Assert.Single(await Merchants());
        Assert.Equal("Lidl", merchant.Name);

        var rows = await Rows(connection);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(merchant.Id, row.MerchantId));
    }

    [Fact]
    public async Task The_same_shop_under_a_different_casing_is_the_same_merchant()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl")]);
        await Sync(connection);

        // What a provider actually does: the enrichment changes its mind about capitals,
        // and a table keyed on the raw string would grow a second Lidl for it.
        _bank.Answer("cursor-2", added: [Row("t2", 4m, merchant: "  LIDL  ")]);
        await Sync(connection);

        var merchant = Assert.Single(await Merchants());

        // The name it was first seen under, not the shouted one. Both rows point at it.
        Assert.Equal("Lidl", merchant.Name);
        Assert.All(await Rows(connection), row => Assert.Equal(merchant.Id, row.MerchantId));
    }

    [Fact]
    public async Task A_logo_that_arrives_later_fills_in_the_merchant_that_had_none()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl")]);
        await Sync(connection);

        Assert.Null(Assert.Single(await Merchants()).LogoUrl);

        _bank.Answer("cursor-2", added: [Row("t2", 4m, merchant: "Lidl", logo: "https://logos/lidl.png")]);
        await Sync(connection);

        // One row still, now with the logo -- which is the whole reason the ledger holds a
        // link and not a copy: the expense filed from t1 shows this too.
        Assert.Equal("https://logos/lidl.png", Assert.Single(await Merchants()).LogoUrl);
    }

    [Fact]
    public async Task A_provider_that_stops_sending_a_logo_does_not_take_it_away()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl", logo: "https://logos/lidl.png")]);
        await Sync(connection);

        _bank.Answer("cursor-2", added: [Row("t2", 4m, merchant: "Lidl")]);
        await Sync(connection);

        // A bad afternoon at the provider is not the shop losing its sign, and blanking the
        // column would take the logo off every row at that shop at once.
        Assert.Equal("https://logos/lidl.png", Assert.Single(await Merchants()).LogoUrl);
    }

    [Fact]
    public async Task A_row_the_provider_named_no_merchant_on_points_at_nothing()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: null, description: "CARD PURCHASE 8891")]);

        await Sync(connection);

        Assert.Empty(await Merchants());
        Assert.Null(Assert.Single(await Rows(connection)).MerchantId);
    }

    [Fact]
    public async Task Filing_carries_the_merchant_onto_the_expense()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl", logo: "https://logos/lidl.png")]);
        await Sync(connection);

        var row = Assert.Single(await Rows(connection));
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var merchant = Assert.Single(await Merchants());
        var filed = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(merchant.Id, filed.MerchantId);
    }

    [Fact]
    public async Task Pointing_a_row_at_an_expense_somebody_typed_in_tells_it_where_it_happened()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl")]);
        await Sync(connection);

        var typed = await TypedExpense(12.50m);
        var row = Assert.Single(await Rows(connection));

        await Inbox.Link(row.Id, new LinkBankTransactionRequest { TransactionId = typed.Id }, Ct);

        var merchant = Assert.Single(await Merchants());
        var linked = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == typed.Id, Ct);

        Assert.Equal(merchant.Id, linked.MerchantId);
    }

    [Fact]
    public async Task An_expense_filed_from_a_bank_row_carries_the_shop_and_its_logo_into_the_feed()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl", logo: "https://logos/lidl.png")]);
        await Sync(connection);

        var row = Assert.Single(await Rows(connection));
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var reader = GetService<ICurrentUser>().User;
        var entry = Assert.Single(await DbContext.Set<Data.Entities.Transaction>()
            .SelectUserActivityDto(reader.Id)
            .ToListAsync(Ct));

        Assert.Equal("Lidl", entry.MerchantName);
        Assert.Equal("https://logos/lidl.png", entry.MerchantLogoUrl);
    }

    [Fact]
    public async Task An_expense_nobody_imported_has_no_shop_to_show()
    {
        await TypedExpense(20m);

        var reader = GetService<ICurrentUser>().User;
        var entry = Assert.Single(await DbContext.Set<Data.Entities.Transaction>()
            .SelectUserActivityDto(reader.Id)
            .ToListAsync(Ct));

        // Null and not empty: it went to a shop nobody wrote down, which is what most of
        // the ledger is.
        Assert.Null(entry.MerchantName);
        Assert.Null(entry.MerchantLogoUrl);
    }

    [Fact]
    public async Task The_inbox_reads_a_rows_logo_back_through_its_merchant()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl", logo: "https://logos/lidl.png")]);
        await Sync(connection);

        var listed = Assert.Single(await (await Inbox.List(new InboxFilter(), Ct))
            .SelectDto()
            .ToListAsync(Ct));
        Assert.Equal("Lidl", listed.MerchantName);
        Assert.Equal("https://logos/lidl.png", listed.LogoUrl);
    }

    [Fact]
    public async Task Searching_a_ledger_for_the_shop_finds_the_expense()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: "Lidl", description: "CARD 8891")]);
        await Sync(connection);

        var row = Assert.Single(await Rows(connection));

        // Named for the bank's line rather than the shop, so the search has nothing but the
        // merchant to match on -- which is the case the join is there for.
        await Inbox.File(row.Id, new FileBankTransactionRequest { Name = "Weekly shop" }, Ct);

        var found = await DbContext.Set<Data.Entities.Transaction>()
            .ApplyFilter(new ActivityFilter { Search = "lidl" })
            .ToListAsync(Ct);

        Assert.Single(found);
    }

    // ---- what a row shows when the provider named no merchant ------------------------

    /// <summary>
    /// The case Plaid's sandbox is entirely made of, and a large share of real rows: no
    /// merchant name and no merchant logo, but an icon for the kind of thing it was.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_merchant_still_has_the_providers_icon_to_show()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added:
        [
            Row("t1", 12.50m,
                merchant: null,
                description: "TOUCHSTONE CLIMBING",
                logo: null,
                categoryIcon: "https://plaid-category-icons.plaid.com/PFC_OTHER.png")
        ]);

        await Sync(connection);

        Assert.Empty(await Merchants());

        var listed = Assert.Single(await (await Inbox.List(new InboxFilter(), Ct))
            .SelectDto()
            .ToListAsync(Ct));

        // Nothing to badge, so the row leads with the icon rather than two initials.
        Assert.Null(listed.LogoUrl);
        Assert.Equal("https://plaid-category-icons.plaid.com/PFC_OTHER.png", listed.Mark);
    }

    [Fact]
    public async Task A_merchants_own_logo_wins_over_the_category_icon()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added:
        [
            Row("t1", 12.50m, merchant: "Lidl",
                logo: "https://logos/lidl.png",
                categoryIcon: "https://plaid-category-icons.plaid.com/PFC_FOOD_AND_DRINK.png")
        ]);

        await Sync(connection);

        var listed = Assert.Single(await (await Inbox.List(new InboxFilter(), Ct))
            .SelectDto()
            .ToListAsync(Ct));

        // The place beats the kind of place: one says where, the other only what sort.
        Assert.Equal("https://logos/lidl.png", listed.Mark);
    }

    [Fact]
    public async Task A_row_with_neither_has_no_mark_at_all()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added: [Row("t1", 12.50m, merchant: null, logo: null)]);

        await Sync(connection);

        var listed = Assert.Single(await (await Inbox.List(new InboxFilter(), Ct))
            .SelectDto()
            .ToListAsync(Ct));

        // Null and not an empty string: an img with an empty src resolves to the page and
        // renders as a broken image, where null renders as initials.
        Assert.Null(listed.Mark);
    }

    /// <summary>
    /// The icon stops at the inbox. An expense badges the payer with where it was spent,
    /// and a category icon there would be claiming to know a place when it knows a kind.
    /// </summary>
    [Fact]
    public async Task The_category_icon_does_not_follow_a_row_onto_the_ledger()
    {
        var connection = await LinkAsync();
        _bank.Answer("cursor-1", added:
        [
            Row("t1", 12.50m, merchant: null,
                categoryIcon: "https://plaid-category-icons.plaid.com/PFC_OTHER.png")
        ]);

        await Sync(connection);

        var row = Assert.Single(await Rows(connection));
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        var reader = GetService<ICurrentUser>().User;
        var entry = Assert.Single(await DbContext.Set<Data.Entities.Transaction>()
            .SelectUserActivityDto(reader.Id)
            .ToListAsync(Ct));

        Assert.Null(entry.MerchantName);
        Assert.Null(entry.MerchantLogoUrl);
    }

    private Task<List<Merchant>> Merchants() =>
        DbContext.Set<Merchant>().AsNoTracking().ToListAsync(Ct);

    /// <summary>An expense somebody wrote down: no bank row behind it, and no merchant.</summary>
    private async Task<Expense> TypedExpense(decimal amount)
    {
        var user = GetService<ICurrentUser>().User;

        var expense = new Expense
        {
            User = user,
            Amount = amount,
            Currency = "USD",
            Name = "Dinner",
            DateTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
        };

        expense.Splits.Add(new TransactionSplit { User = user, Amount = amount });

        DbContext.Add(expense);
        await DbContext.SaveChangesAsync(Ct);

        return expense;
    }

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
}
