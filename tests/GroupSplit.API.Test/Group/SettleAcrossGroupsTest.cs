using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Settling with a person rather than inside a group.
/// </summary>
/// <remarks>
/// A balance belongs to a group and a payment belongs to a person, and those two facts were
/// never reconciled: somebody owing the same friend in two groups had to settle twice, in
/// two dialogs, on two pages. The plan adds the balances up by person; settling with one
/// spends the payment back across the groups it came from, in one save.
/// <para>
/// The property that runs through all of these is the one the per-group settling already
/// had: every transfer written has the caller on one end. Adding up across groups does not
/// give anybody the right to record money moving between two other people.
/// </para>
/// </remarks>
public class SettleAcrossGroupsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// The whole point in one test: two groups, one person, one line.
    /// </summary>
    [Fact]
    public async Task ThePlan_AddsWhatOnePersonOwesAcrossEveryGroup()
    {
        var friend = await CreateNewUser();

        // Home: the friend pays 40, split evenly, so the caller owes 20.
        var home = await GroupWith("Home", friend);
        await Expense(home, 40, paidBy: friend);

        // Vacation: the friend pays 100, split evenly, so the caller owes 50.
        var vacation = await GroupWith("Vacation", friend);
        await Expense(vacation, 100, paidBy: friend);

        var plan = await Settlements().GetPlan(Ct);

        var line = Assert.Single(plan.YouPay);

        Assert.Equal(friend.Id, line.UserId);
        Assert.Equal(70, line.Amount);

        // And the groups it comes from, so the arithmetic is checkable on the row rather
        // than by opening two groups.
        Assert.Equal(2, line.Groups.Count);
        Assert.Equal(70, line.Groups.Sum(part => part.Amount));
        Assert.Contains(line.Groups, part => part.GroupName == "Home" && part.Amount == 20);
        Assert.Contains(line.Groups, part => part.GroupName == "Vacation" && part.Amount == 50);
    }

    /// <summary>
    /// The two gross sides stay apart, as they do on the home page's position: owing one
    /// person 20 and being owed 50 by another is two facts, not a net of 30.
    /// </summary>
    [Fact]
    public async Task ThePlan_KeepsTheTwoSidesApart()
    {
        var owed = await CreateNewUser();
        var owing = await CreateNewUser();

        var first = await GroupWith("First", owed);
        await Expense(first, 40, paidBy: owed);

        var second = await GroupWith("Second", owing);
        await Expense(second, 100);

        var plan = await Settlements().GetPlan(Ct);

        Assert.Equal(20, plan.YouOweTotal);
        Assert.Equal(50, plan.OwedToYouTotal);
        Assert.Equal(30, plan.Net);

        Assert.Equal(owed.Id, Assert.Single(plan.YouPay).UserId);
        Assert.Equal(owing.Id, Assert.Single(plan.OwedToYou).UserId);
    }

    /// <summary>
    /// Per group first and then added up, which is the only order that gives an answer
    /// anybody can act on. Minimising across the union would produce a line between two
    /// people who are not in a group together and have no reason to move money to each
    /// other.
    /// </summary>
    [Fact]
    public async Task ThePlan_NeverPairsPeopleWhoShareNoGroup()
    {
        var one = await CreateNewUser();
        var two = await CreateNewUser();

        var first = await GroupWith("First", one);
        await Expense(first, 40, paidBy: one);

        var second = await GroupWith("Second", two);
        await Expense(second, 40);

        var plan = await Settlements().GetPlan(Ct);

        Assert.All(plan.YouPay.Concat(plan.OwedToYou), line =>
            Assert.All(line.Groups, part => Assert.NotEqual(Guid.Empty, part.GroupId)));

        Assert.Equal(one.Id, Assert.Single(plan.YouPay).UserId);
        Assert.Equal(two.Id, Assert.Single(plan.OwedToYou).UserId);
    }

    [Fact]
    public async Task ThePlan_WhenSquareEverywhere_IsEmptyOnBothSides()
    {
        var friend = await CreateNewUser();
        await GroupWith("Home", friend);

        var plan = await Settlements().GetPlan(Ct);

        Assert.Empty(plan.YouPay);
        Assert.Empty(plan.OwedToYou);
        Assert.Equal(0, plan.Net);
        Assert.Null(plan.LastSettled);
    }

    /// <summary>
    /// One payment, two groups, and a transfer in each -- because a balance cannot be
    /// cleared from outside the group it belongs to.
    /// </summary>
    [Fact]
    public async Task Settling_WithOnePerson_ClearsThemInEveryGroup()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 40, paidBy: friend);

        var vacation = await GroupWith("Vacation", friend);
        await Expense(vacation, 100, paidBy: friend);

        var recorded = await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 70,
            Direction = SettlementDirection.YouPaidThem
        }, Ct);

        Assert.Equal(70, recorded.Amount);
        Assert.Equal(2, recorded.Groups.Count);

        Assert.Equal(0, await BalanceOf(home, Me.Id));
        Assert.Equal(0, await BalanceOf(vacation, Me.Id));

        // One transfer per group, each with the caller on one end.
        var transfers = await DbContext.Set<Transfer>().ToListAsync(Ct);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, transfer => Assert.Equal(Me.Id, transfer.UserId));
    }

    /// <summary>
    /// Both directions, because both are the caller's to state: money they sent and money
    /// they were sent.
    /// </summary>
    [Fact]
    public async Task Settling_RecordsMoneyComingInAsWellAsGoingOut()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 60);

        await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 30,
            Direction = SettlementDirection.TheyPaidYou
        }, Ct);

        Assert.Equal(0, await BalanceOf(home, Me.Id));

        var transfer = Assert.Single(await DbContext.Set<Transfer>().ToListAsync(Ct));

        Assert.Equal(friend.Id, transfer.UserId);
    }

    /// <summary>
    /// Part of a debt spanning two groups clears the larger one first. The caller does not
    /// choose, and that is the point of the screen: which group a payment lands in is
    /// bookkeeping, and the person handing over the money should not have to do it.
    /// </summary>
    [Fact]
    public async Task Settling_PartOfIt_ClearsTheLargestGroupFirst()
    {
        var friend = await CreateNewUser();

        var small = await GroupWith("Small", friend);
        await Expense(small, 40, paidBy: friend);

        var large = await GroupWith("Large", friend);
        await Expense(large, 100, paidBy: friend);

        // 50 of the 70 outstanding: enough to clear the larger group and nothing else.
        var recorded = await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 50,
            Direction = SettlementDirection.YouPaidThem
        }, Ct);

        Assert.Equal(50, Assert.Single(recorded.Groups).Amount);
        Assert.Equal(large, Assert.Single(recorded.Groups).GroupId);

        Assert.Equal(0, await BalanceOf(large, Me.Id));
        Assert.Equal(-20, await BalanceOf(small, Me.Id));
    }

    /// <summary>
    /// Somebody rounding up. It has to land somewhere, and the largest group is where an
    /// overpayment is least surprising to find.
    /// </summary>
    [Fact]
    public async Task Settling_MoreThanIsOutstanding_PutsTheRestOnTheLargestGroup()
    {
        var friend = await CreateNewUser();

        var small = await GroupWith("Small", friend);
        await Expense(small, 40, paidBy: friend);

        var large = await GroupWith("Large", friend);
        await Expense(large, 100, paidBy: friend);

        var recorded = await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 80,
            Direction = SettlementDirection.YouPaidThem
        }, Ct);

        Assert.Equal(80, recorded.Groups.Sum(part => part.Amount));

        var largest = recorded.Groups.Single(part => part.GroupId == large);

        Assert.Equal(60, largest.Amount);
        Assert.Equal(10, await BalanceOf(large, Me.Id));
    }

    /// <summary>
    /// The direction is stated rather than read off the balance, so a debtor recording "I
    /// paid you back" is refused when they are the one owed, rather than quietly writing a
    /// second debt.
    /// </summary>
    [Fact]
    public async Task Settling_InTheWrongDirection_IsRefused()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 60);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleWithPerson(new SettleWithPersonRequest
            {
                UserId = friend.Id,
                Amount = 30,
                Direction = SettlementDirection.YouPaidThem
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, exception.Code);
    }

    [Fact]
    public async Task Settling_WithSomebodyYouAreSquareWith_IsRefused()
    {
        var friend = await CreateNewUser();
        await GroupWith("Home", friend);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleWithPerson(new SettleWithPersonRequest
            {
                UserId = friend.Id,
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, exception.Code);
    }

    [Fact]
    public async Task Settling_WithYourself_IsRefused()
    {
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleWithPerson(new SettleWithPersonRequest
            {
                UserId = Me.Id,
                Amount = 10
            }, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementWithSelf, exception.Code);
    }

    /// <summary>
    /// The date and the note go onto every transfer it writes, so a month closed on the 3rd
    /// holds the payments that closed it -- in each of the groups the payment spanned.
    /// </summary>
    [Fact]
    public async Task Settling_WithADateAndANote_PutsThemOnEveryTransfer()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 40, paidBy: friend);

        var vacation = await GroupWith("Vacation", friend);
        await Expense(vacation, 100, paidBy: friend);

        var endOfSeptember = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 70,
            Direction = SettlementDirection.YouPaidThem,
            Date = endOfSeptember,
            Description = "  bank transfer  "
        }, Ct);

        var transfers = await DbContext.Set<Transfer>().ToListAsync(Ct);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, transfer =>
        {
            Assert.Equal(endOfSeptember, transfer.DateTime);
            Assert.Equal("bank transfer", transfer.Description);
        });
    }

    /// <summary>
    /// The history is what answers "did I already pay this?", and it spans groups because
    /// the payment did.
    /// </summary>
    [Fact]
    public async Task TheHistory_ListsEveryRepaymentTheCallerWasPartyTo()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 40, paidBy: friend);

        var vacation = await GroupWith("Vacation", friend);
        await Expense(vacation, 100, paidBy: friend);

        await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 70,
            Direction = SettlementDirection.YouPaidThem
        }, Ct);

        var history = await (await Settlements().GetHistory(Ct)).ToListAsync(Ct);

        Assert.Equal(2, history.Count);
        Assert.All(history, settlement =>
        {
            Assert.True(settlement.PaidByYou);
            Assert.Equal(Me.Id, settlement.FromUserId);
            Assert.Equal(friend.Id, settlement.ToUserId);
        });

        Assert.Contains(history, settlement => settlement.GroupName == "Home");
        Assert.Contains(history, settlement => settlement.GroupName == "Vacation");
    }

    /// <summary>
    /// A repayment between two other members belongs to the group's own history, not to a
    /// list of payments the caller made or received.
    /// </summary>
    [Fact]
    public async Task TheHistory_LeavesOutRepaymentsBetweenTwoOtherMembers()
    {
        var one = await CreateNewUser();
        var two = await CreateNewUser();

        var group = await GroupWith("Trip", one, two);

        await Settlements().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = one.Id,
            ToUserId = two.Id,
            Amount = 25
        }, Ct);

        Assert.Empty(await (await Settlements().GetHistory(Ct)).ToListAsync(Ct));
    }

    /// <summary>
    /// When the caller last settled anywhere, so the screen can say it without opening
    /// every group in turn.
    /// </summary>
    [Fact]
    public async Task ThePlan_RemembersWhenYouLastSettled()
    {
        var friend = await CreateNewUser();

        var home = await GroupWith("Home", friend);
        await Expense(home, 40, paidBy: friend);

        var whenItMoved = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = friend.Id,
            Amount = 20,
            Direction = SettlementDirection.YouPaidThem,
            Date = whenItMoved
        }, Ct);

        var plan = await Settlements().GetPlan(Ct);

        Assert.Equal(whenItMoved, plan.LastSettled);
    }

    // ---- setup -----------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private ISettlementService Settlements() => GetService<ISettlementService>();

    private async Task<Guid> GroupWith(string name, params Data.Entities.User[] members)
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = name }, Ct);

        await JoinGroup(group.Id, members);

        return group.Id;
    }

    /// <summary>An expense split evenly, paid by the caller unless somebody else is named.</summary>
    private Task Expense(Guid groupId, decimal amount, Data.Entities.User? paidBy = null) =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            PaidByUserId = paidBy?.Id ?? Me.Id,
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow
        }, Ct).AsTask();

    private async Task<decimal> BalanceOf(Guid groupId, Guid userId)
    {
        var balances = await GetService<IGroupService>().GetGroupNetBalance(groupId, Ct);

        return await balances
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstAsync(Ct);
    }
}
