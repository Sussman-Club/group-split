using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Settling up through a day (#247): "clear everything up to the end of August", while what
/// came after stays open.
/// </summary>
/// <remarks>
/// The rule every test here leans on: through a day, a balance counts the expenses dated up
/// to it and <em>every settlement, whatever its date</em>. Money paid goes against the
/// oldest debt first. Cutting settlements off at the day as well would make somebody who paid
/// in September pay August a second time when August is settled in October -- which is the
/// test called <see cref="A_repayment_made_after_the_day_still_counts_against_it"/>.
/// </remarks>
public class SettleThroughTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static readonly DateOnly EndOfJuly = new(2026, 7, 31);
    private static readonly DateOnly EndOfAugust = new(2026, 8, 31);

    private static DateTimeOffset On(int month, int day) => new(2026, month, day, 12, 0, 0, TimeSpan.Zero);

    // ---- What is owed through a day ------------------------------------------------------

    [Fact]
    public async Task Settling_up_through_a_day_leaves_out_expenses_dated_after_it()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Expense(group, 40, On(9, 10));

        var settled = await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);

        var payment = Assert.Single(settled.Payments);
        Assert.Equal(other.Id, payment.FromUserId);
        Assert.Equal(50, payment.Amount);
        Assert.Equal(EndOfAugust, settled.CoversThrough);

        // September's half of the 40 is still owed, as it should be.
        Assert.Equal(-20, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// The case that decides the model. August's 50 was paid on 15 September with the plain
    /// Settle button. Settling August in October must find nothing left, even though the
    /// payment is dated after the line.
    /// </summary>
    [Fact]
    public async Task A_repayment_made_after_the_day_still_counts_against_it()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await TheyPaidMe(group, other, 50, On(9, 15));
        await Expense(group, 40, On(9, 10));

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct));

        Assert.Equal(ErrorCodes.SettlementNothingToSettle, refused.Code);
    }

    [Fact]
    public async Task Settling_the_same_period_twice_finds_nothing_the_second_time()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct));

        Assert.Equal(ErrorCodes.SettlementNothingToSettle, refused.Code);
    }

    [Fact]
    public async Task Without_a_day_settling_up_clears_everything_and_claims_no_period()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Expense(group, 40, On(9, 10));

        var settled = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal(70, Assert.Single(settled.Payments).Amount);
        Assert.Null(settled.CoversThrough);
        Assert.Equal(0, await BalanceOf(group, other.Id));
        Assert.All(await Transfers(group), transfer => Assert.Null(transfer.CoversThrough));
    }

    /// <summary>
    /// 02:00 UTC on 1 September is 22:00 on 31 August in New York. The day ends on the
    /// caller's calendar, the same way the date filters end it.
    /// </summary>
    [Fact]
    public async Task The_day_ends_at_midnight_on_the_callers_calendar()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.Zero));

        var inUtc = await (await GetService<IGroupService>().GetGroupNetBalance(group,
            new Cutoff(EndOfAugust), Ct)).FirstAsync(balance => balance.UserId == other.Id, Ct);
        var inNewYork = await (await GetService<IGroupService>().GetGroupNetBalance(group,
            new Cutoff(EndOfAugust, -240), Ct)).FirstAsync(balance => balance.UserId == other.Id, Ct);

        Assert.Equal(0, inUtc.Balance);
        Assert.Equal(-50, inNewYork.Balance);
    }

    [Fact]
    public async Task Every_payment_a_settling_up_writes_remembers_the_day()
    {
        var (group, _, third) = await GroupOfThree();

        await Expense(group, 90, On(8, 5));
        await Expense(group, 30, On(8, 6), paidBy: third);

        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);

        var transfers = await Transfers(group);
        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, transfer => Assert.Equal(EndOfAugust, transfer.CoversThrough));
    }

    // ---- The cross-group plan and settling with one person -------------------------------

    [Fact]
    public async Task The_plan_through_a_day_owes_only_what_that_period_left()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Expense(group, 40, On(9, 10));

        var whole = await Settlements().GetPlan(Ct);
        var august = await Settlements().GetPlan(new Cutoff(EndOfAugust), Ct);

        Assert.Equal(70, Assert.Single(whole.OwedToYou).Amount);
        Assert.Equal(50, Assert.Single(august.OwedToYou, person => person.UserId == other.Id).Amount);
    }

    [Fact]
    public async Task Settling_with_a_person_through_a_day_records_the_day_on_what_it_writes()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));

        var settled = await Settlements().SettleWithPerson(new SettleWithPersonRequest
        {
            UserId = other.Id,
            Amount = 50,
            Direction = SettlementDirection.TheyPaidYou,
            Through = EndOfAugust
        }, Ct);

        Assert.Equal(EndOfAugust, settled.CoversThrough);
        Assert.Equal(EndOfAugust, Assert.Single(await Transfers(group)).CoversThrough);
    }

    [Fact]
    public async Task Settling_with_a_person_for_a_period_already_paid_is_refused()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await TheyPaidMe(group, other, 50, On(9, 2));
        await Expense(group, 40, On(9, 10));

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleWithPerson(new SettleWithPersonRequest
            {
                UserId = other.Id,
                Amount = 20,
                Direction = SettlementDirection.TheyPaidYou,
                Through = EndOfAugust
            }, Ct));

        Assert.Equal(ErrorCodes.SettlementNothingToSettle, refused.Code);
    }

    [Fact]
    public async Task A_single_settlement_remembers_the_day_it_was_given()
    {
        var (group, other) = await GroupOfTwo();

        await GetService<IGroupService>().Settle(group, new SettleRequest
        {
            UserId = other.Id,
            Amount = 10,
            Direction = SettlementDirection.TheyPaidYou,
            Through = EndOfAugust
        }, Ct);

        Assert.Equal(EndOfAugust, Assert.Single(await Transfers(group)).CoversThrough);
    }

    // ---- What a settlement covers, read afterwards ---------------------------------------

    [Fact]
    public async Task A_settled_period_reads_as_square_since_the_group_began()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);

        var coverage = await Settlements().GetCoverage(Assert.Single(await Transfers(group)).Id, 0, Ct);

        Assert.NotNull(coverage);
        Assert.Equal(EndOfAugust, coverage.Through);
        Assert.Null(coverage.From);
        Assert.Equal(0, coverage.StillOwed);
    }

    /// <summary>
    /// The case a stored list of "covered" expenses gets wrong. An expense dated inside a
    /// period that was already settled is still owed, and the settlement says so.
    /// </summary>
    [Fact]
    public async Task An_expense_back_dated_into_a_settled_period_shows_as_still_owed()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);
        var settlement = Assert.Single(await Transfers(group));

        await Expense(group, 60, On(8, 25));

        var coverage = await Settlements().GetCoverage(settlement.Id, 0, Ct);

        Assert.Equal(30, coverage!.StillOwed);
    }

    [Fact]
    public async Task A_period_starts_the_day_after_the_last_one_the_same_two_people_settled()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, On(7, 20));
        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfJuly }, Ct);

        await Expense(group, 60, On(8, 20));
        await Settlements().SettleUp(group, new SettleUpRequest { Through = EndOfAugust }, Ct);

        var august = (await Transfers(group)).Single(transfer => transfer.CoversThrough == EndOfAugust);

        var coverage = await Settlements().GetCoverage(august.Id, 0, Ct);

        Assert.Equal(new DateOnly(2026, 8, 1), coverage!.From);
    }

    [Fact]
    public async Task A_settlement_that_claims_no_period_has_no_coverage()
    {
        var (group, other) = await GroupOfTwo();

        await TheyPaidMe(group, other, 10, On(9, 1));

        Assert.Null(await Settlements().GetCoverage(Assert.Single(await Transfers(group)).Id, 0, Ct));
    }

    // ---- Editing the day -------------------------------------------------------------------

    [Fact]
    public async Task A_settlements_day_can_be_changed_and_cleared()
    {
        var (group, other) = await GroupOfTwo();

        await TheyPaidMe(group, other, 10, On(9, 1));
        var id = Assert.Single(await Transfers(group)).Id;
        var transactions = GetService<ITransactionService>();

        var model = (await transactions.GetUpdateModel(id, Ct))!;
        model.CoversThrough = EndOfAugust;
        await transactions.Update(id, model, Ct);

        Assert.Equal(EndOfAugust, (await transactions.GetUpdateModel(id, Ct))!.CoversThrough);

        model.CoversThrough = null;
        await transactions.Update(id, model, Ct);

        Assert.Null((await Reloaded(id)).CoversThrough);
    }

    [Fact]
    public async Task An_expense_cannot_be_given_a_day_it_covers_through()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, On(8, 20));
        var id = await DbContext.Set<Expense>().Where(expense => expense.GroupId == group)
            .Select(expense => expense.Id).SingleAsync(Ct);
        var transactions = GetService<ITransactionService>();

        var model = (await transactions.GetUpdateModel(id, Ct))!;
        model.CoversThrough = EndOfAugust;

        var refused = await Assert.ThrowsAsync<ValidationException>(() => transactions.Update(id, model, Ct).AsTask());

        Assert.Equal(ErrorCodes.ValidationFailed, refused.Code);
    }

    // ---- Fixtures --------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private ISettlementService Settlements() => GetService<ISettlementService>();

    private async Task<(Guid Group, Data.Entities.User Other)> GroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group.Id, other);
    }

    private async Task<(Guid Group, Data.Entities.User Other, Data.Entities.User Third)> GroupOfThree()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();
        var third = await CreateNewUser();

        await JoinGroup(group.Id, other, third);

        return (group.Id, other, third);
    }

    /// <summary>An expense split evenly, paid by the caller unless somebody else is named.</summary>
    private async Task Expense(Guid groupId, decimal amount, DateTimeOffset date, Data.Entities.User? paidBy = null)
    {
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            PaidByUserId = paidBy?.Id ?? Me.Id,
            Name = "Dinner",
            Amount = amount,
            DateTime = date
        }, Ct);
    }

    /// <summary>The other member paying the caller back, with the plain Settle button.</summary>
    private async Task TheyPaidMe(Guid groupId, Data.Entities.User other, decimal amount, DateTimeOffset date)
    {
        await GetService<IGroupService>().Settle(groupId, new SettleRequest
        {
            UserId = other.Id,
            Amount = amount,
            Direction = SettlementDirection.TheyPaidYou,
            Date = date
        }, Ct);
    }

    private async Task<List<Transfer>> Transfers(Guid groupId) =>
        await DbContext.Set<Transfer>().AsNoTracking()
            .Where(transfer => transfer.GroupId == groupId)
            .ToListAsync(Ct);

    private async Task<Transfer> Reloaded(Guid id) =>
        await DbContext.Set<Transfer>().AsNoTracking().SingleAsync(transfer => transfer.Id == id, Ct);

    private async Task<decimal> BalanceOf(Guid groupId, Guid userId)
    {
        var balances = await GetService<IGroupService>().GetGroupNetBalance(groupId, Ct);

        return await balances
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstAsync(Ct);
    }
}
