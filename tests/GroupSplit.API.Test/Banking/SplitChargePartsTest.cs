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
/// Writing to a charge that was split into several expenses.
/// </summary>
/// <remarks>
/// One charge filed as several expenses leaves them sharing one piece of paper, and until
/// these existed nothing anywhere wrote to such a bill: both calculator fixtures stamp every
/// line with one expense, and the inbox's own split tests stop at the amounts. So the three
/// routes that take a whole bill on behalf of one expense -- saving it, deleting it, claiming
/// a line of it -- were each doing exactly what they said to all of it, and the suite was
/// green.
/// <para>
/// Kept apart from <c>InboxServiceTest</c>, which is about turning imported rows into
/// expenses. These are about what may be done to the bill afterwards, which is a different
/// subject with the same setup.
/// </para>
/// </remarks>
public class SplitChargePartsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    private IReceiptService Receipts => GetService<IReceiptService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

    /// <summary>
    /// One part of a split charge cannot save the bill.
    /// </summary>
    /// <remarks>
    /// The route takes a whole bill on behalf of one expense, and on a shared paper that is
    /// never what anybody meant: <c>Apply</c> drops every line the request did not name --
    /// which is every sibling's, with its claims -- and re-points the ones it did at the
    /// caller's expense.
    /// <para>
    /// And no safe call existed. The route refuses a request whose total is not this
    /// expense's amount, and a part's amount is never the whole paper's total while a sibling
    /// holds anything, so every request that could have got as far as <c>Apply</c> was one
    /// that would have destroyed the others.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_part_of_a_split_charge_cannot_rewrite_the_bill()
    {
        var (groceries, jacket) = await SplitCharge();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Receipts.SaveForExpense(groceries, new SaveReceiptRequest
            {
                Subtotal = 60m,
                Total = 60m,
                Items = [new ReceiptItemInput { Name = "GROCERIES", TotalPrice = 60m }]
            }, Ct));

        Assert.Equal(ErrorCodes.BankTransactionAlreadyFiled, refusal.Code);

        // And the other part still has its line, which is the thing that was being lost.
        var theirs = await Lines(jacket);

        Assert.Equal("JACKET", Assert.Single(theirs).Name);
    }

    /// <summary>
    /// One part of a split charge cannot take its lines off the bill either.
    /// </summary>
    /// <remarks>
    /// It removed this part's lines and left the figures at the foot of the paper, which are
    /// the charge's. Afterwards the items no longer came to the bill's own subtotal, and
    /// <c>RefuseIfFiguresDisagree</c> runs at the head of every division -- so every untouched
    /// sibling stopped being dividable, for a reason none of their owners caused and none of
    /// them could undo.
    /// </remarks>
    [Fact]
    public async Task One_part_of_a_split_charge_cannot_take_its_lines_off_the_bill()
    {
        var (groceries, jacket) = await SplitCharge();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Receipts.DeleteForExpense(groceries, Ct));

        Assert.Equal(ErrorCodes.BankTransactionAlreadyFiled, refusal.Code);

        // The bill still describes the charge, so the sibling can still be divided by it.
        var bill = await Receipts.ForExpense(jacket, Ct);

        Assert.Equal(bill.Subtotal, bill.Items.Sum(line => line.TotalPrice));
        Assert.Equal(2, bill.Items.Count);
    }

    /// <summary>
    /// Claiming reaches this expense's part of the bill and no further.
    /// </summary>
    /// <remarks>
    /// The line was looked up across the whole paper and the claimants were then validated
    /// against the caller's own group -- so somebody could rewrite who had a line belonging to
    /// another expense of the same charge, in another group or in no group at all. Naming
    /// somebody who is in both, which whoever paid always is, re-apportioned the other's
    /// balances on its next division with nothing raised anywhere.
    /// </remarks>
    [Fact]
    public async Task Claiming_a_line_reaches_this_expense_and_no_further()
    {
        var (groceries, jacket) = await SplitCharge();

        var theirs = Assert.Single(await Lines(jacket));

        await Assert.ThrowsAsync<NotFoundException>(
            () => Receipts.SetClaims(groceries, theirs.Id, new SetReceiptItemClaimsRequest
            {
                Claims = [new ReceiptClaimInput { UserId = Self }]
            }, Ct));

        // Its own line still answers, so the refusal is about whose line it is and not about
        // the route being shut.
        var mine = Assert.Single(await Lines(groceries));

        await Receipts.SetClaims(groceries, mine.Id, new SetReceiptItemClaimsRequest
        {
            Claims = [new ReceiptClaimInput { UserId = Self }]
        }, Ct);
    }

    /// <summary>
    /// A part is stored with the amount the division will re-derive for it.
    /// </summary>
    /// <remarks>
    /// Two functions price a part -- one from the request, before any expense exists, one from
    /// the placed bill -- and they broke the tie on a leftover cent differently, because
    /// <c>Largest</c> favours the lower key and they are keyed differently. The split cut a
    /// part to one figure and the itemised rule then re-derived the other and refused the
    /// expense the split had just made.
    /// <para>
    /// The shape here makes that deterministic rather than a coin flip. The largest part by
    /// line value is exempt, so neither pricing can favour it and both fall back to the first
    /// key of the taxable ones -- which is request order for one and bill order for the other.
    /// Listing the parts in the opposite order to the lines is what makes those differ, and
    /// the odd cent of tax is what there is to disagree about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_part_is_cut_to_the_figure_its_own_division_works_out()
    {
        var (group, category, _) = await ItemizedGroup();
        var row = await Row(160.01m);

        // Exempt and largest, then the two taxable ones -- in this order on the paper.
        var bill = await Receipts.SaveForBankRow(row.Id, new SaveReceiptRequest
        {
            Subtotal = 160m,
            Tax = 0.01m,
            Total = 160.01m,
            Items =
            [
                Line("RICE", 100m, taxable: false),
                Line("CIDER", 30m),
                Line("BEER", 30m)
            ]
        }, Ct);

        var rice = bill.Items.First(line => line.Name == "RICE");
        var cider = bill.Items.First(line => line.Name == "CIDER");
        var beer = bill.Items.First(line => line.Name == "BEER");

        // Asked for in the other order, so the two pricings see the taxable parts in
        // different sequences and the leftover cent lands in different places.
        var split = await Inbox.Split(row.Id, new SplitBankTransactionRequest
        {
            Parts =
            [
                Part("Rice", group.Id, category, rice.Id),
                Part("Beer", group.Id, category, beer.Id),
                Part("Cider", group.Id, category, cider.Id)
            ]
        }, Ct);

        Assert.Equal(160.01m, split.Parts.Sum(part => part.Amount));

        // And every part is dividable by its own bill afterwards, which is the thing the
        // disagreement broke: each expense's shares have to come to the amount it was cut to.
        foreach (var part in split.Parts)
        {
            var shares = await DbContext.Set<TransactionSplit>()
                .Where(entry => entry.TransactionId == part.TransactionId)
                .ToListAsync(Ct);

            Assert.Equal(part.Amount, shares.Sum(entry => entry.Amount));
        }
    }

    /// <summary>
    /// Reading one part's bill says what the other parts came to and never what they were.
    /// </summary>
    /// <remarks>
    /// The parts of a split charge can be in different groups -- one purchase the flat's, the
    /// other somebody's own, which is most of the reason to split one -- and the reading
    /// projected every line of the paper to whoever could see any one of them: what was
    /// bought, for how much, and each claimant by name, which falls back to an email address.
    /// <para>
    /// The caller is only ever proved entitled to the part they asked about. What the rest
    /// came to is still reported, because a bill totalling more than the expense it was
    /// opened from is the first thing anybody queries.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Reading_one_part_reports_the_others_without_naming_them()
    {
        var (groceries, _) = await SplitCharge();

        var bill = await Receipts.ForExpense(groceries, Ct);
        var response = await Receipts.ResponseFor(bill, groceries, Ct);

        var line = Assert.Single(response.Items);

        Assert.Equal("GROCERIES", line.Name);
        Assert.DoesNotContain(response.Items, item => item.Name == "JACKET");

        // And what it came to, so the paper still adds up for the reader.
        Assert.Equal(1, response.ElsewhereItemCount);
        Assert.Equal(40m, response.ElsewhereTotal);
        Assert.Equal(100m, response.Total);
    }

    /// <summary>
    /// A bill nobody has filed is read whole, which is the point of reading it.
    /// </summary>
    /// <remarks>
    /// Nothing on an unfiled charge belongs to anybody yet, and deciding which lines are
    /// which purchase is exactly what the reader is about to do.
    /// </remarks>
    [Fact]
    public async Task A_bill_on_a_charge_nobody_has_filed_is_read_whole()
    {
        var row = await Row(100m);

        var bill = await Receipts.SaveForBankRow(row.Id, new SaveReceiptRequest
        {
            Subtotal = 100m,
            Total = 100m,
            Items = [Line("GROCERIES", 60m), Line("JACKET", 40m)]
        }, Ct);

        var response = await Receipts.ResponseFor(bill, null, Ct);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal(0, response.ElsewhereItemCount);
        Assert.Equal(0m, response.ElsewhereTotal);
    }

    /// <summary>
    /// The division preview quotes this expense's part, not the whole paper.
    /// </summary>
    /// <remarks>
    /// The CLI renders it straight into the confirmation it asks before dividing, and into the
    /// machine-readable request an agent relays to its human. A consent gate naming three
    /// times the money about to move is worse than one naming none.
    /// </remarks>
    [Fact]
    public async Task The_preview_quotes_this_expenses_part_of_the_bill()
    {
        var (groceries, _) = await SplitCharge();

        var division = await Receipts.Preview(groceries, Ct);

        Assert.Equal(60m, division.Total);
        Assert.Equal(60m, division.Shares.Sum(share => share.Amount));
    }

    /// <summary>
    /// A 100.00 charge split in two: 60.00 of groceries for the group, a 40.00 jacket for
    /// nobody but the payer. Answers the two expenses' ids.
    /// </summary>
    private async Task<(Guid Groceries, Guid Jacket)> SplitCharge()
    {
        var (group, category, _) = await ItemizedGroup();
        var row = await Row(100m);

        var bill = await Receipts.SaveForBankRow(row.Id, new SaveReceiptRequest
        {
            Subtotal = 100m,
            Total = 100m,
            Items = [Line("GROCERIES", 60m), Line("JACKET", 40m)]
        }, Ct);

        var split = await Inbox.Split(row.Id, new SplitBankTransactionRequest
        {
            Parts =
            [
                Part("Groceries", group.Id, category,
                    bill.Items.First(line => line.Name == "GROCERIES").Id),
                Part("Jacket", group.Id, category,
                    bill.Items.First(line => line.Name == "JACKET").Id)
            ]
        }, Ct);

        return (
            split.Parts.Single(part => part.Name == "Groceries").TransactionId,
            split.Parts.Single(part => part.Name == "Jacket").TransactionId);
    }

    /// <summary>The lines of the bill that are one expense's.</summary>
    private async Task<IReadOnlyList<ReceiptItem>> Lines(Guid expenseId) =>
        ReceiptSplitCalculator.PartOf(await Receipts.ForExpense(expenseId, Ct), expenseId);

    private ReceiptItemInput Line(string name, decimal price, bool taxable = true) =>
        new()
        {
            Name = name,
            TotalPrice = price,
            IsTaxable = taxable,
            Claims = [new ReceiptClaimInput { UserId = Self }]
        };

    private static BankTransactionPartInput Part(
        string name, Guid groupId, Guid categoryId, Guid itemId) =>
        new() { Name = name, GroupId = groupId, CategoryId = categoryId, ItemIds = [itemId] };

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount)
    {
        var connection = new BankConnection
        {
            User = GetService<ICurrentUser>().User,
            Provider = FakeBankConnector.Name,
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext =
                GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
            LinkedAt = DateTimeOffset.UtcNow
        };

        connection.Accounts.Add(new LinkedAccount
        {
            ProviderAccountId = "acc-1",
            Name = "Everyday",
            Type = "depository"
        });

        var row = new BankTransaction
        {
            Account = connection.Accounts.First(),
            ProviderTransactionId = $"t-{Guid.NewGuid():N}",
            Date = new DateOnly(2026, 9, 1),
            Amount = amount,
            Currency = "USD",
            Description = "COSTCO WHOLESALE 718",
            MerchantName = "Costco",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(connection);
        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }

    private async Task<(Data.Entities.Group Group, Guid Category, Data.Entities.User Other)>
        ItemizedGroup()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Home" }, Ct);

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
            Name = "Shopping",
            DefaultSplitRuleId = rule.Id
        }, Ct);

        return (group, category.Id, other);
    }
}
