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
/// Splitting a charge nobody itemised.
/// </summary>
/// <remarks>
/// The trolley and the jacket are two purchases whether or not anybody typed the receipt in,
/// and until this the only way to say so was to type the whole bill first -- twenty lines of
/// groceries nobody will ever read, to answer a question about two of them. So the parts say
/// what they are worth instead.
/// <para>
/// Which moves an invariant. A bill makes the parts sum to the charge by construction, since
/// they are cuts of one figure; stated amounts are four numbers somebody typed in a car park,
/// and every check here exists because nothing else stands between a slip of the thumb and a
/// group balance that is wrong by the difference.
/// </para>
/// </remarks>
public class SplitChargeWithoutABillTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    private IReceiptService Receipts => GetService<IReceiptService>();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

    /// <summary>
    /// The warehouse run with no receipt typed in: the flat's groceries and a jacket of your
    /// own, on one charge.
    /// </summary>
    [Fact]
    public async Task A_charge_with_no_bill_is_split_into_the_amounts_it_is_given()
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var split = await Inbox.Split(row.Id, new SplitBankTransactionRequest
        {
            Parts =
            [
                new BankTransactionPartInput
                {
                    Name = "Groceries", GroupId = group.Id, CategoryId = category, Amount = 65.50m
                },
                // No group at all, which is the whole reason to split this charge.
                new BankTransactionPartInput { Name = "Jacket", Amount = 34.50m }
            ]
        }, Ct);

        Assert.Equal(100m, split.Charge);
        Assert.Equal([65.50m, 34.50m], split.Parts.Select(part => part.Amount));

        // No lines, and the response says so rather than leaving the count to be read as
        // "one line each".
        Assert.All(split.Parts, part => Assert.Equal(0, part.ItemCount));

        var filed = await DbContext.Set<Expense>()
            .Where(expense => expense.BankTransactionId == row.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, filed.Count);
        Assert.Null(filed.Single(expense => expense.Name == "Jacket").GroupId);

        Assert.Equal(BankTransactionStatus.Filed,
            (await DbContext.Set<BankTransaction>().FindAsync([row.Id], Ct))!.Status);
    }

    /// <summary>
    /// Amounts that do not come to the charge are refused, and nothing is filed.
    /// </summary>
    /// <remarks>
    /// The check the bill used to make unnecessary. Without it the group is charged for
    /// whatever was typed and the difference goes nowhere -- there is no third expense and no
    /// row left waiting to say a penny of this was never accounted for.
    /// </remarks>
    [Fact]
    public async Task Parts_that_do_not_come_to_the_charge_are_refused()
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var refusal = await Assert.ThrowsAsync<UnprocessableException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    Part("Groceries", group.Id, category, 65.50m),
                    Part("Jacket", null, null, 34.49m)
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsDoNotSumToCharge, refusal.Code);
        Assert.Equal(99.99m, refusal.Extensions["placed"]);
        Assert.Equal(0.01m, refusal.Extensions["unplaced"]);

        await NothingWasFiled(row);
    }

    /// <summary>
    /// There is no part that takes whatever is left over.
    /// </summary>
    /// <remarks>
    /// Tempting, and the one figure nobody would have checked -- which is the figure most
    /// worth checking, since the whole point of splitting a charge is that somebody is about
    /// to be asked to pay for a piece of it.
    /// </remarks>
    [Fact]
    public async Task A_part_has_to_say_what_it_is_worth()
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    Part("Groceries", group.Id, category, 65.50m),
                    Part("The rest", null, null, amount: null)
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsInvalid, refusal.Code);
        Assert.Equal(new[] { 1 }, refusal.Extensions["partsWithoutAnAmount"]);

        await NothingWasFiled(row);
    }

    /// <summary>
    /// A part is worth more than nothing.
    /// </summary>
    /// <remarks>
    /// Both of these sum to the charge as long as another part makes up for them, so the
    /// check above would pass either. A part of zero is an expense that is not a purchase --
    /// the very thing <c>[NotZero]</c> refuses on the ordinary create route -- and a negative
    /// one is a refund rather than a piece of this payment.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task A_part_worth_nothing_or_less_is_refused(decimal empty)
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    Part("Groceries", group.Id, category, 100m - empty),
                    Part("Jacket", null, null, empty)
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsInvalid, refusal.Code);
        Assert.Equal(new[] { 1 }, refusal.Extensions["emptyParts"]);

        await NothingWasFiled(row);
    }

    /// <summary>
    /// A part cannot name lines on a charge that has none.
    /// </summary>
    /// <remarks>
    /// A caller in the wrong mode, which is worth saying out loud rather than quietly
    /// ignoring: the ids came from somewhere, and somewhere is a bill on a different charge.
    /// </remarks>
    [Fact]
    public async Task A_part_cannot_name_lines_when_there_is_no_bill()
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    new BankTransactionPartInput
                    {
                        Name = "Groceries",
                        GroupId = group.Id,
                        CategoryId = category,
                        Amount = 65.50m,
                        ItemIds = [Guid.NewGuid()]
                    },
                    Part("Jacket", null, null, 34.50m)
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsInvalid, refusal.Code);

        await NothingWasFiled(row);
    }

    /// <summary>
    /// And the other way round: a part cannot state an amount when the charge has a bill.
    /// </summary>
    /// <remarks>
    /// The figure the bill would have cut is the one that matters, so a stated one could only
    /// ever agree with it by luck or disagree with it silently. Refused rather than ignored,
    /// because a caller that sent it believed it.
    /// </remarks>
    [Fact]
    public async Task A_part_cannot_state_an_amount_when_the_charge_has_a_bill()
    {
        var (group, category, _) = await EvenGroup();
        var row = await Row(100m);

        var bill = await Receipts.SaveForBankRow(row.Id, new SaveReceiptRequest
        {
            Subtotal = 100m,
            Total = 100m,
            Items =
            [
                new ReceiptItemInput { Name = "GROCERIES", TotalPrice = 60m },
                new ReceiptItemInput { Name = "JACKET", TotalPrice = 40m }
            ]
        }, Ct);

        var refusal = await Assert.ThrowsAsync<ValidationException>(
            () => Inbox.Split(row.Id, new SplitBankTransactionRequest
            {
                Parts =
                [
                    new BankTransactionPartInput
                    {
                        Name = "Groceries",
                        GroupId = group.Id,
                        CategoryId = category,
                        Amount = 60m,
                        ItemIds = [bill.Items.First(line => line.Name == "GROCERIES").Id]
                    },
                    new BankTransactionPartInput
                    {
                        Name = "Jacket",
                        ItemIds = [bill.Items.First(line => line.Name == "JACKET").Id]
                    }
                ]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitPartsInvalid, refusal.Code);

        await NothingWasFiled(row);
    }

    /// <summary>
    /// The preview prices a charge with no bill too, so the screen asks one question.
    /// </summary>
    /// <remarks>
    /// The figures are the caller's own and the useful half of the answer is the other one:
    /// what is still in no part. A part nobody has filled in yet is worth nothing, which is
    /// what it is.
    /// </remarks>
    [Fact]
    public async Task The_preview_says_what_is_still_unplaced_on_a_charge_with_no_bill()
    {
        var row = await Row(100m);

        var preview = await Inbox.PreviewSplit(row.Id, new SplitChargePreviewRequest
        {
            Parts =
            [
                new SplitChargePreviewPartInput { Amount = 65.50m },
                new SplitChargePreviewPartInput()
            ]
        }, Ct);

        Assert.Equal([65.50m, 0m], preview.Amounts);
        Assert.Equal(100m, preview.Charge);
        Assert.Equal(65.50m, preview.Placed);
        Assert.Equal(34.50m, preview.Unplaced);
    }

    /// <summary>
    /// The refusals above are worth nothing if the row is filed anyway, and the whole split
    /// commits at once -- so one part landing in the ledger would mean all of them did.
    /// </summary>
    private async Task NothingWasFiled(BankTransaction row)
    {
        Assert.Empty(await DbContext.Set<Expense>()
            .Where(expense => expense.BankTransactionId == row.Id)
            .ToListAsync(Ct));

        Assert.Equal(BankTransactionStatus.New,
            (await DbContext.Set<BankTransaction>().FindAsync([row.Id], Ct))!.Status);
    }

    private static BankTransactionPartInput Part(
        string name, Guid? groupId, Guid? categoryId, decimal? amount) =>
        new() { Name = name, GroupId = groupId, CategoryId = categoryId, Amount = amount };

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
        EvenGroup()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Home" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group, await CreateEvenCategory(group.Id, "Shopping"), other);
    }
}
