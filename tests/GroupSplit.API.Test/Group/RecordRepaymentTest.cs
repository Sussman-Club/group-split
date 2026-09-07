using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Recording one repayment between two named members, whichever of them the caller is.
/// </summary>
/// <remarks>
/// The deliberate exception to the rule the rest of settling keeps: settle and settle-up both
/// put the caller on one end, and this does not. What it buys is a group able to enter a
/// ledger it already agreed on -- months closed years ago elsewhere, whose repayments nobody
/// present is party to.
/// </remarks>
public class RecordRepaymentTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// The whole point in one test: money between two other members, recorded by somebody who
    /// is neither, and both of their balances move by it.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_BetweenTwoOtherMembers_MovesBothBalances()
    {
        var (group, other, third) = await GroupOfThree();

        // A debt entirely between the other two: one pays 60 and only they two are in it.
        await Expense(group, 60, paidBy: other, between: [other, third]);

        Assert.Equal(30, await BalanceOf(group, other.Id));
        Assert.Equal(-30, await BalanceOf(group, third.Id));

        var recorded = await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = third.Id,
            ToUserId = other.Id,
            Amount = 30
        }, Ct);

        Assert.Equal(third.Id, recorded.FromUserId);
        Assert.Equal(other.Id, recorded.ToUserId);
        Assert.Equal(30, recorded.Amount);

        Assert.Equal(0, await BalanceOf(group, other.Id));
        Assert.Equal(0, await BalanceOf(group, third.Id));

        // And the caller, who was party to none of it, is untouched.
        Assert.Equal(0, await BalanceOf(group, Me.Id));
    }

    /// <summary>
    /// It writes the same transfer pressing Settle would have written, so a repayment entered
    /// this way is not a second kind of thing to reason about.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_WritesAnOrdinaryTransfer()
    {
        var (group, other, third) = await GroupOfThree();

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = other.Id,
            ToUserId = third.Id,
            Amount = 25
        }, Ct);

        var transfer = Assert.Single(await DbContext.Set<Transfer>()
            .Include(candidate => candidate.Splits)
            .Where(candidate => candidate.GroupId == group)
            .ToListAsync(Ct));

        Assert.Equal("Settlement", transfer.Name);
        Assert.Equal(25, transfer.Amount);
        Assert.Equal(other.Id, transfer.UserId);
        Assert.Equal(third.Id, Assert.Single(transfer.Splits).UserId);
    }

    /// <summary>
    /// The caller may still be one of the two, which is what makes this a generalisation of
    /// settling rather than a separate thing that happens to look like it.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_WithTheCallerOnOneEnd_IsAllowed()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = other.Id,
            ToUserId = Me.Id,
            Amount = 50
        }, Ct);

        Assert.Equal(0, await BalanceOf(group, Me.Id));
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// The date is the reason this exists at all for a migration: a group entering months that
    /// closed years ago needs the repayments dated then, not the day somebody typed them in.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_WithADateAndANote_PutsThemOnTheTransfer()
    {
        var (group, other, third) = await GroupOfThree();

        var endOfAugust = new DateTimeOffset(2024, 8, 31, 12, 0, 0, TimeSpan.Zero);

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = other.Id,
            ToUserId = third.Id,
            Amount = 40,
            Date = endOfAugust,
            Description = "  Settle up August 2024  "
        }, Ct);

        var transfer = Assert.Single(await DbContext.Set<Transfer>()
            .Where(candidate => candidate.GroupId == group)
            .ToListAsync(Ct));

        Assert.Equal(endOfAugust, transfer.DateTime);
        Assert.Equal("Settle up August 2024", transfer.Description);
    }

    /// <summary>
    /// Nothing is checked against what is outstanding. A group entering its history writes
    /// repayments for months whose expenses are already in, and the balance is cumulative:
    /// what matters is that the total lands, not that each one fits the balance at the time.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_MoreThanIsOutstanding_IsRecorded()
    {
        var (group, other) = await GroupOfTwo();

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = other.Id,
            ToUserId = Me.Id,
            Amount = 500
        }, Ct);

        Assert.Equal(-500, await BalanceOf(group, Me.Id));
        Assert.Equal(500, await BalanceOf(group, other.Id));
    }

    [Fact]
    public async Task RecordRepayment_WithTheSamePersonOnBothEnds_Throws()
    {
        var (group, other) = await GroupOfTwo();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().RecordRepayment(group, new RecordRepaymentRequest
            {
                FromUserId = other.Id,
                ToUserId = other.Id,
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementWithSelf, exception.Code);
    }

    /// <summary>
    /// Both ends have to be in the group. Naming an outsider is the way this would otherwise
    /// write a balance nobody in the group could ever clear.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_WithSomebodyOutsideTheGroup_Throws()
    {
        var (group, other) = await GroupOfTwo();

        var outsider = await CreateNewUser();

        var payer = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().RecordRepayment(group, new RecordRepaymentRequest
            {
                FromUserId = outsider.Id,
                ToUserId = other.Id,
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.UserNotFound, payer.Code);

        var payee = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().RecordRepayment(group, new RecordRepaymentRequest
            {
                FromUserId = other.Id,
                ToUserId = outsider.Id,
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.UserNotFound, payee.Code);
    }

    /// <summary>
    /// Scoped to the caller's own groups, like everything else: a group they are not in is not
    /// found, rather than one they can write repayments into.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_GroupNotFound_Throws()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().RecordRepayment(Guid.NewGuid(), new RecordRepaymentRequest
            {
                FromUserId = Guid.NewGuid(),
                ToUserId = Guid.NewGuid(),
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.GroupNotFound, exception.Code);
    }

    /// <summary>
    /// A settling-up counts one of these the way it counts a repayment recorded by hand: the
    /// balance is cumulative, so what is left is simply the difference.
    /// </summary>
    [Fact]
    public async Task RecordRepayment_IsCountedByASubsequentSettleUp()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = other.Id,
            ToUserId = Me.Id,
            Amount = 30
        }, Ct);

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal(20, Assert.Single(settled.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private ISettlementService Settlements() => GetService<ISettlementService>();

    private async Task<(Guid Group, Data.Entities.User Other)> GroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Repayment Group" }, Ct);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group.Id, other);
    }

    private async Task<(Guid Group, Data.Entities.User Other, Data.Entities.User Third)> GroupOfThree()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Repayment Group" }, Ct);

        var other = await CreateNewUser();
        var third = await CreateNewUser();

        await JoinGroup(group.Id, other, third);

        return (group.Id, other, third);
    }

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
