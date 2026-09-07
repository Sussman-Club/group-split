using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Settling your own position in a group up, in one action.
/// </summary>
/// <remarks>
/// The property that runs through all of these: every transfer it writes has the caller on
/// one end. A member can say what they paid and what they were paid, because both are things
/// they were there for; nobody may record two other members squaring up between themselves.
/// </remarks>
public class SettleUpTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// The whole point in one test: one action, and the caller owes nobody and is owed by
    /// nobody.
    /// </summary>
    [Fact]
    public async Task SettleUp_ZeroesTheCallersPosition()
    {
        var (group, other) = await GroupOfTwo();

        // The caller pays 100 for a dinner split evenly, so the other member owes them 50.
        await Expense(group, 100);

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var payment = Assert.Single(settled.Payments);

        Assert.Equal(other.Id, payment.FromUserId);
        Assert.Equal(Me.Id, payment.ToUserId);
        Assert.Equal(50, payment.Amount);

        Assert.Equal(0, await BalanceOf(group, Me.Id));
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// Both directions, because both are the caller's to state: money they sent and money
    /// they were sent. What is not theirs to state is money moving between two other people,
    /// which is the next test.
    /// </summary>
    [Fact]
    public async Task SettleUp_RecordsWhatIsOwedBothWays()
    {
        var (group, other, third) = await GroupOfThree();

        // The caller pays 90 split three ways, so each of the others owes them 30.
        await Expense(group, 90);

        // The third member pays 60 split three ways, so the caller owes them 20.
        await Expense(group, 60, paidBy: third);

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        // Netted per pair by the minimiser: the caller is owed 30 by one and 10 by the other
        // after their own 20 is set against it.
        Assert.All(settled.Payments, payment =>
            Assert.True(payment.FromUserId == Me.Id || payment.ToUserId == Me.Id));

        Assert.Equal(0, await BalanceOf(group, Me.Id));
    }

    /// <summary>
    /// The reason this is personal. Two other members owing each other is not the caller's to
    /// record, and nothing it writes may name two people who are not them.
    /// </summary>
    [Fact]
    public async Task SettleUp_NeverRecordsAPaymentBetweenTwoOtherMembers()
    {
        var (group, other, third) = await GroupOfThree();

        // A debt entirely between the other two: one pays, and the caller is not in the split
        // for it because they are the payer of nothing here.
        await Expense(group, 60, paidBy: other, between: [other, third]);

        // And something that involves the caller, so there is a payment to write at all.
        await Expense(group, 30, paidBy: third);

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.All(settled.Payments, payment =>
            Assert.True(payment.FromUserId == Me.Id || payment.ToUserId == Me.Id,
                "A settling-up wrote a payment the caller was not party to."));

        // The other two are left to square up between themselves, which is theirs to do.
        Assert.Equal(0, await BalanceOf(group, Me.Id));
        Assert.NotEqual(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// It writes transfers and nothing else, so a settling-up is exactly what pressing Settle
    /// once per person would have written.
    /// </summary>
    [Fact]
    public async Task SettleUp_WritesOneTransferPerCounterparty()
    {
        var (group, _, third) = await GroupOfThree();

        await Expense(group, 90);
        await Expense(group, 30, paidBy: third);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var transfers = await DbContext.Set<Transfer>()
            .Where(transfer => transfer.GroupId == group)
            .ToListAsync(Ct);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, transfer => Assert.Equal("Settlement", transfer.Name));
    }

    /// <summary>
    /// A settling-up needs no bookkeeping of its own because the balance is cumulative: a
    /// second one over a position already at zero has nothing left to write and says so.
    /// </summary>
    [Fact]
    public async Task SettleUp_Twice_HasNothingLeftToSettle()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var again = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, again.Code);
    }

    /// <summary>
    /// An expense recorded after a settling-up is simply outstanding, and the next one
    /// settles it. Nothing had to remember the first, which is what dropping the bookkeeping
    /// bought.
    /// </summary>
    [Fact]
    public async Task SettleUp_AfterMoreSpending_SettlesOnlyWhatIsLeft()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        await Expense(group, 60);

        var second = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal(30, Assert.Single(second.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// A repayment somebody already recorded by hand is money that has moved, so what is left
    /// is the difference. This falls out of the balance being cumulative rather than out of
    /// anything the settling-up does.
    /// </summary>
    [Fact]
    public async Task SettleUp_CountsRepaymentsAlreadyRecorded()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        await GetService<IGroupService>().Settle(group,
            new SettleRequest { UserId = other.Id, Amount = 30 }, Ct);

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal(20, Assert.Single(settled.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// The date and the note go onto every repayment it writes. The date is what lets a month
    /// closed on the 3rd hold the payments that closed it.
    /// </summary>
    [Fact]
    public async Task SettleUp_WithADateAndANote_PutsThemOnEveryRepayment()
    {
        var (group, _, third) = await GroupOfThree();

        await Expense(group, 90);
        await Expense(group, 30, paidBy: third);

        var endOfSeptember = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        var settled = await Settlements().SettleUp(group,
            new SettleUpRequest { Date = endOfSeptember, Description = "  end of September  " }, Ct);

        Assert.Equal(endOfSeptember, settled.Date);
        Assert.Equal("end of September", settled.Description);

        var transfers = await DbContext.Set<Transfer>()
            .Where(transfer => transfer.GroupId == group)
            .ToListAsync(Ct);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, transfer =>
        {
            Assert.Equal(endOfSeptember, transfer.DateTime);
            Assert.Equal("end of September", transfer.Description);
        });
    }

    [Fact]
    public async Task SettleUp_WithNoDate_IsRecordedNow()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        var before = DateTimeOffset.UtcNow;

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.InRange(settled.Date, before, DateTimeOffset.UtcNow);
        Assert.Null(settled.Description);
    }

    [Fact]
    public async Task SettleUp_WhenAlreadySquare_Throws()
    {
        var (group, _) = await GroupOfTwo();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, exception.Code);
    }

    [Fact]
    public async Task SettleUp_GroupNotFound_Throws()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().SettleUp(Guid.NewGuid(), new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.GroupNotFound, exception.Code);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private ISettlementService Settlements() => GetService<ISettlementService>();

    private async Task<(Guid Group, Data.Entities.User Other)> GroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Settle Up Group" }, Ct);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group.Id, other);
    }

    private async Task<(Guid Group, Data.Entities.User Other, Data.Entities.User Third)> GroupOfThree()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Settle Up Group" }, Ct);

        var other = await CreateNewUser();
        var third = await CreateNewUser();

        await JoinGroup(group.Id, other, third);

        return (group.Id, other, third);
    }

    /// <summary>
    /// An expense in the group, split evenly between everybody unless <paramref name="between"/>
    /// names a smaller set, and paid by the caller unless somebody else is named.
    /// </summary>
    private async Task Expense(Guid groupId, decimal amount, Data.Entities.User? paidBy = null,
        Data.Entities.User[]? between = null)
    {
        var splits = between is null
            ? null
            : between
                .Select(member => new SplitInput
                {
                    UserId = member.Id,
                    Amount = Math.Round(amount / between.Length, 2)
                })
                .ToList();

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            PaidByUserId = paidBy?.Id ?? Me.Id,
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            Splits = splits
        }, Ct);
    }

    private async Task<decimal> BalanceOf(Guid groupId, Guid userId)
    {
        var balances = await GetService<IGroupService>().GetGroupNetBalance(groupId, Ct);

        return await balances
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstAsync(Ct);
    }
}
