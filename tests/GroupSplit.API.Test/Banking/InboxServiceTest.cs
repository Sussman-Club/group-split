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
        Assert.Equal(0, (await Inbox.Summary(ct: Ct)).NewCount);

        await Inbox.Restore(row.Id, Ct);
        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
        Assert.Equal(1, (await Inbox.Summary(ct: Ct)).NewCount);
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
        Assert.Equal(1, (await Inbox.Summary(ct: Ct)).NewCount);
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
        Assert.Equal(0, (await stranger.Summary(ct: Ct)).NewCount);
    }

    // ---- setup ---------------------------------------------------------------------------

    /// <summary>A group of two with a category that divides evenly, which is the ordinary case.</summary>
    // ---- A bill typed against the row, before anybody filed it -------------------------
    //
    // The reason a receipt may point at a bank row at all: the card is charged at the
    // restaurant, the row lands in the inbox, and who had what is settled at the table while
    // everybody still remembers. Filing has to carry that onto the expense *before* it
    // divides it, because a category that divides by the bill reads the bill.

    /// <summary>
    /// An itemised category divides a filed row by the bill that was already on it.
    /// </summary>
    /// <remarks>
    /// This failed for the whole of the feature's first shape: the carry-over ran after the
    /// expense was created, and creating it is what divides it -- so filing answered "there
    /// is no bill on it yet" about a bill the person had just typed.
    /// </remarks>
    [Fact]
    public async Task Filing_into_an_itemized_category_divides_by_the_bill_already_on_the_row()
    {
        var (group, category, other) = await GroupWithItemizedCategory();
        var row = await Row(amount: 30m);

        await BillOn(row.Id, ("Steak", 20m, Self), ("Pasta", 10m, other.Id));

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest
        {
            GroupId = group.Id,
            CategoryId = category
        }, Ct);

        var splits = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(20m, splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(10m, splits.Single(split => split.UserId == other.Id).Amount);
        Assert.Equal(30m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Filing hands the bill from the row to the expense. Exactly one of the two owns it --
    /// which is what the check constraint says and what makes both cascades coherent.
    /// </summary>
    [Fact]
    public async Task Filing_moves_the_bill_off_the_row_and_onto_the_expense()
    {
        var (group, category, _) = await GroupWithEvenCategory();
        var row = await Row(amount: 30m);

        await BillOn(row.Id, ("Everything", 30m, Self));

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest
        {
            GroupId = group.Id,
            CategoryId = category
        }, Ct);

        // The bill stays on the row it was typed against; which purchases it became is on
        // its lines.
        var bill = await DbContext.Set<Receipt>().AsNoTracking()
            .Include(receipt => receipt.Items)
            .FirstAsync(receipt => receipt.BankTransactionId == row.Id, Ct);

        Assert.All(bill.Items, item => Assert.Equal(expense.Id, item.ExpenseId));
    }

    /// <summary>
    /// Linking a row to an expense that already has a bill of its own leaves the row's bill
    /// alone -- claims included.
    /// </summary>
    /// <remarks>
    /// The claims are the point. Working out whether to take the bill used to prune it first,
    /// against the group it was not being filed into, and the pruning was committed even when
    /// the hand-over was declined -- so declining to take a bill silently un-claimed it.
    /// </remarks>
    [Fact]
    public async Task Linking_to_an_expense_that_has_its_own_bill_leaves_the_rows_bill_untouched()
    {
        var (group, category, other) = await GroupWithEvenCategory();

        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 30m,
            DateTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            GroupId = group.Id,
            CategoryId = category
        }, Ct);

        await GetService<IReceiptService>().SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 30m,
            Total = 30m,
            Items = [new ReceiptItemInput { Name = "Its own", TotalPrice = 30m }]
        }, Ct);

        var row = await Row(amount: 30m);

        // Claimed by somebody outside the destination group, which is what the pruning bug
        // used to reach for.
        var stranger = await CreateNewUser();
        await BillOn(row.Id, ("Row's own", 30m, stranger.Id));

        await Inbox.Link(row.Id, new LinkBankTransactionRequest { TransactionId = expense.Id }, Ct);

        var rowsBill = await DbContext.Set<Receipt>().AsNoTracking()
            .Include(receipt => receipt.Items)
            .ThenInclude(item => item.Claims)
            .FirstAsync(receipt => receipt.BankTransactionId == row.Id, Ct);

        Assert.Single(rowsBill.Items.Single().Claims);
        Assert.Equal(stranger.Id, rowsBill.Items.Single().Claims.Single().UserId);
    }

    /// <summary>
    /// Filing a row without picking a group leaves its bill exactly where it was -- claims
    /// and all.
    /// </summary>
    /// <remarks>
    /// A personal expense is shared with nobody, so a bill on one could never be divided.
    /// Taking it across used to prune every claim against an empty roster and commit that, so
    /// somebody who itemised five lines at the table and filed the row personally lost all of
    /// it without a word.
    /// </remarks>
    [Fact]
    public async Task Filing_a_row_personally_leaves_its_bill_and_its_claims_on_the_row()
    {
        var row = await Row(amount: 30m);

        await BillOn(row.Id, ("Everything", 30m, Self));

        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        Assert.Null(expense.GroupId);

        var bill = await DbContext.Set<Receipt>().AsNoTracking()
            .Include(receipt => receipt.Items)
            .ThenInclude(item => item.Claims)
            .FirstAsync(receipt => receipt.BankTransactionId == row.Id, Ct);

        Assert.Single(bill.Items.Single().Claims);
        Assert.Null(bill.Items.Single().ExpenseId);
    }

    /// <summary>
    /// A bill filing declined to take can be removed.
    /// </summary>
    /// <remarks>
    /// Filing a row personally leaves its bill on the row, and the row is then filed -- which
    /// file and link both refuse. Without a delete the bill would sit in the inbox forever
    /// with overwriting it as the only thing anybody could do to it.
    /// </remarks>
    [Fact]
    public async Task A_bill_left_behind_by_a_personal_filing_can_be_deleted()
    {
        var row = await Row(amount: 30m);

        await BillOn(row.Id, ("Everything", 30m, Self));
        await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        await GetService<IReceiptService>().DeleteForBankRow(row.Id, Ct);

        Assert.Empty(await DbContext.Set<Receipt>().AsNoTracking()
            .Where(receipt => receipt.BankTransactionId == row.Id).ToListAsync(Ct));
    }

    // ---- One charge, two purchases --------------------------------------------------
    //
    // The warehouse run: the flat's groceries and a jacket that is nobody's business but
    // yours, on one card charge. Filing it whole would put the clothes in the group's ledger
    // and file them under Groceries; splitting gives each part its own expense.

    /// <summary>
    /// The parts are cut from the charge, each with its own group, and they sum to what the
    /// card was charged.
    /// </summary>
    [Fact]
    public async Task Splitting_a_charge_files_it_as_several_expenses_that_sum_to_it()
    {
        var (group, category, other) = await GroupWithEvenCategory();
        var row = await Row(amount: 100m);

        var bill = await BillOn(row.Id, ("GROCERIES", 60m, Self), ("JACKET", 40m, Self));
        var jacket = bill.Items.First(item => item.Name == "JACKET");
        var groceries = bill.Items.First(item => item.Name == "GROCERIES");

        var split = await Inbox.Split(row.Id, new SplitBankTransactionRequest
        {
            Parts =
            [
                new BankTransactionPartInput
                {
                    Name = "Groceries", GroupId = group.Id, CategoryId = category,
                    ItemIds = [groceries.Id]
                },
                new BankTransactionPartInput { Name = "Jacket", ItemIds = [jacket.Id] }
            ]
        }, Ct);

        Assert.Equal(2, split.Parts.Count);
        Assert.Equal(100m, split.Parts.Sum(part => part.Amount));

        var shared = split.Parts.Single(part => part.GroupId == group.Id);
        var mine = split.Parts.Single(part => part.GroupId is null);

        Assert.Equal(60m, shared.Amount);
        Assert.Equal(40m, mine.Amount);

        // The groceries are the flat's and divide between them; the jacket is the payer's
        // alone, on their own ledger.
        var sharedSplits = await DbContext.Set<TransactionSplit>()
            .Where(entry => entry.TransactionId == shared.TransactionId).ToListAsync(Ct);

        Assert.Equal(2, sharedSplits.Count);
        Assert.Contains(sharedSplits, entry => entry.UserId == other.Id);

        var mineSplits = await DbContext.Set<TransactionSplit>()
            .Where(entry => entry.TransactionId == mine.TransactionId).ToListAsync(Ct);

        Assert.Equal(Self, Assert.Single(mineSplits).UserId);
        Assert.Equal(BankTransactionStatus.Filed, (await Reload(row)).Status);
    }

    /// <summary>
    /// Tax follows the lines it was charged on, and the parts still sum to the charge.
    /// </summary>
    /// <remarks>
    /// The bill that makes splitting worth doing: groceries exempt, general goods not.
    /// Weighing the tax across every line would tax the bananas and let the jacket off.
    /// </remarks>
    [Fact]
    public async Task Tax_lands_on_the_part_whose_lines_were_charged_it()
    {
        var (group, category, _) = await GroupWithEvenCategory();
        var row = await Row(amount: 104m);

        var bill = await GetService<IReceiptService>().SaveForBankRow(row.Id,
            new SaveReceiptRequest
            {
                Subtotal = 100m,
                Tax = 4m,
                Total = 104m,
                Items =
                [
                    new ReceiptItemInput
                    {
                        Name = "GROCERIES", TotalPrice = 60m, IsTaxable = false,
                        Split = ReceiptItemSplit.Evenly
                    },
                    new ReceiptItemInput
                    {
                        Name = "JACKET", TotalPrice = 40m,
                        Claims = [new ReceiptClaimInput { UserId = Self }]
                    }
                ]
            }, Ct);

        var jacket = bill.Items.First(item => item.Name == "JACKET");
        var groceries = bill.Items.First(item => item.Name == "GROCERIES");

        var split = await Inbox.Split(row.Id, new SplitBankTransactionRequest
        {
            Parts =
            [
                new BankTransactionPartInput
                {
                    Name = "Groceries", GroupId = group.Id, CategoryId = category,
                    ItemIds = [groceries.Id]
                },
                new BankTransactionPartInput { Name = "Jacket", ItemIds = [jacket.Id] }
            ]
        }, Ct);

        // All 4.00 of the tax is the jacket's: the groceries were exempt.
        Assert.Equal(60m, split.Parts.Single(part => part.GroupId == group.Id).Amount);
        Assert.Equal(44m, split.Parts.Single(part => part.GroupId is null).Amount);
        Assert.Equal(104m, split.Parts.Sum(part => part.Amount));
    }

    /// <summary>
    /// A line in no part is money no part accounts for, so the parts would stop summing to
    /// the charge. Refused by name, and nothing is filed.
    /// </summary>
    [Fact]
    public async Task A_split_that_does_not_account_for_every_line_is_refused()
    {
        var row = await Row(amount: 100m);
        var bill = await BillOn(row.Id, ("GROCERIES", 60m, Self), ("JACKET", 40m, Self));
        var one = bill.Items.First();

        var thrown = await Assert.ThrowsAsync<ValidationException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    new BankTransactionPartInput { Name = "One", ItemIds = [one.Id] },
                    new BankTransactionPartInput { Name = "Two", ItemIds = [one.Id] }
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsInvalid, thrown.Code);
        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
    }

    /// <summary>A charge with no bill has nothing to divide up.</summary>
    [Fact]
    public async Task A_charge_with_no_bill_cannot_be_split()
    {
        var row = await Row(amount: 100m);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    new BankTransactionPartInput { Name = "One", ItemIds = [Guid.NewGuid()] },
                    new BankTransactionPartInput { Name = "Two", ItemIds = [Guid.NewGuid()] }
                ]
            }, Ct));
    }

    private Guid Self => GetService<ICurrentUser>().User.Id;

    /// <summary>A bill typed against an imported row, before anybody files it.</summary>
    private Task<Receipt> BillOn(Guid rowId, params (string Name, decimal Price, Guid Had)[] lines) =>
        GetService<IReceiptService>().SaveForBankRow(rowId, new SaveReceiptRequest
        {
            Subtotal = lines.Sum(line => line.Price),
            Total = lines.Sum(line => line.Price),
            Items =
            [
                .. lines.Select(line => new ReceiptItemInput
                {
                    Name = line.Name,
                    TotalPrice = line.Price,
                    Claims = [new ReceiptClaimInput { UserId = line.Had }]
                })
            ]
        }, Ct);

    private async Task<(Data.Entities.Group Group, Guid Category, Data.Entities.User Other)>
        GroupWithItemizedCategory()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Dinners" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var rule = await GetService<ISplitRuleService>().Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "By the bill",
            Definition = new ItemizedSplitRuleDto()
        }, Ct);

        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = group.Id,
            Name = "Dinners out",
            DefaultSplitRuleId = rule.Id
        }, Ct);

        return (group, category.Id, other);
    }

    private async Task<(Data.Entities.Group Group, Guid Category, Data.Entities.User Other)> GroupWithEvenCategory()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);
        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group, await CreateEvenCategory(group.Id), other);
    }

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    // ---- Narrowing the inbox to a span of days -----------------------------------------
    //
    // A bank sends months at a time, and the way through a backlog is a month of it at a
    // time. The span is days rather than instants because that is what a bank puts on a row.

    [Fact]
    public async Task Listing_between_two_days_keeps_both_of_them()
    {
        await Row(10m, providerId: "before", date: new DateOnly(2026, 8, 31));
        await Row(20m, providerId: "first", date: new DateOnly(2026, 9, 1));
        await Row(30m, providerId: "last", date: new DateOnly(2026, 9, 30));
        await Row(40m, providerId: "after", date: new DateOnly(2026, 10, 1));

        var rows = await Listed(new InboxFilter(
            From: new DateOnly(2026, 9, 1),
            To: new DateOnly(2026, 9, 30)));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.ProviderTransactionId == "first");
        Assert.Contains(rows, row => row.ProviderTransactionId == "last");
    }

    [Fact]
    public async Task A_span_open_at_one_end_bounds_only_the_other()
    {
        await Row(10m, providerId: "august", date: new DateOnly(2026, 8, 31));
        await Row(20m, providerId: "october", date: new DateOnly(2026, 10, 1));

        var since = await Listed(new InboxFilter(From: new DateOnly(2026, 9, 1)));
        var until = await Listed(new InboxFilter(To: new DateOnly(2026, 9, 1)));

        Assert.Equal("october", Assert.Single(since).ProviderTransactionId);
        Assert.Equal("august", Assert.Single(until).ProviderTransactionId);
    }

    [Fact]
    public async Task No_span_at_all_is_every_row_the_status_matches()
    {
        await Row(10m, providerId: "old", date: new DateOnly(2020, 1, 1));
        await Row(20m, providerId: "new", date: new DateOnly(2030, 1, 1));

        Assert.Equal(2, (await Listed(new InboxFilter())).Count);
    }

    /// <summary>
    /// The span narrows what the status already chose rather than replacing it: somebody
    /// working through last month's backlog is looking at last month's waiting rows.
    /// </summary>
    [Fact]
    public async Task The_span_and_the_status_both_apply()
    {
        var ignored = await Row(10m, providerId: "ignored", date: new DateOnly(2026, 9, 10));
        await Row(20m, providerId: "waiting", date: new DateOnly(2026, 9, 10));
        await Row(30m, providerId: "elsewhere", date: new DateOnly(2026, 11, 10));

        await SetStatus(ignored, BankTransactionStatus.Ignored);

        var september = new InboxFilter(From: new DateOnly(2026, 9, 1), To: new DateOnly(2026, 9, 30));

        Assert.Equal("waiting", Assert.Single(await Listed(september)).ProviderTransactionId);

        Assert.Equal("ignored",
            Assert.Single(await Listed(september with { Status = InboxStatus.Ignored })).ProviderTransactionId);
    }

    /// <summary>
    /// A card charge often posts days after it was spent, and the row shows the authorized
    /// date. Matching on the posting date instead would put a row dated the 30th of August
    /// inside September, under a heading naming a span its own date is outside of.
    /// </summary>
    [Fact]
    public async Task The_span_matches_the_date_the_row_shows()
    {
        await Row(10m, providerId: "spent-in-august",
            date: new DateOnly(2026, 9, 2), authorizedDate: new DateOnly(2026, 8, 30));

        await Row(20m, providerId: "spent-in-september",
            date: new DateOnly(2026, 10, 2), authorizedDate: new DateOnly(2026, 9, 30));

        var september = await Listed(new InboxFilter(
            From: new DateOnly(2026, 9, 1),
            To: new DateOnly(2026, 9, 30)));

        Assert.Equal("spent-in-september", Assert.Single(september).ProviderTransactionId);
    }

    private async Task<List<BankTransaction>> Listed(InboxFilter filter) =>
        await (await Inbox.List(filter, Ct)).ToListAsync(Ct);

    private async Task<BankTransaction> Row(decimal amount, string? merchant = "Lidl",
        string description = "LIDL 1234", string currency = "USD", string providerId = "t1",
        DateOnly? date = null, DateOnly? authorizedDate = null)
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
            Date = date ?? new DateOnly(2026, 9, 1),
            AuthorizedDate = authorizedDate,
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
