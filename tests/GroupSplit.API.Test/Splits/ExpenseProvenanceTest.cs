using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Correcting the account of what divided an expense, without restating what anybody owed.
/// </summary>
/// <remarks>
/// The two operations here exist because a migration had to guess. A ledger imported from a
/// workbook that never knew about versions arrived with every categorised expense pointing
/// at its category's only version, so an expense from March 2023 claims to have been divided
/// by a ratio the group agreed in 2026. Once the rule's real history is written the dates on
/// the rows settle it.
/// <para>
/// The property every test here is really about is the one in
/// <see cref="Reattaching_leaves_every_balance_in_the_group_exactly_as_it_was"/>: a pointer
/// is not a division, and correcting one may not move a single cent.
/// </para>
/// </remarks>
public class ExpenseProvenanceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IExpenseProvenance Provenance => GetService<IExpenseProvenance>();

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private ITransactionService Transactions => GetService<ITransactionService>();

    private static DateTimeOffset On(int year, int month, int day = 1) =>
        new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var self = GetService<ICurrentUser>().User.Id;

        var group = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    private static SharesSplitRuleDto Shares(Guid a, int weightA, Guid b, int weightB) =>
        new() { Shares = new Dictionary<Guid, int> { [a] = weightA, [b] = weightB } };

    private Task<Expense> AnExpense(Guid groupId, Guid? categoryId, decimal amount, DateTimeOffset on) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = amount,
            DateTime = on,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct).AsTask();

    private async Task<Guid?> VersionOn(Guid transactionId) =>
        await DbContext.Set<Data.Entities.Transaction>()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.SplitRuleVersionId)
            .FirstAsync(Ct);

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .OrderBy(split => split.UserId)
            .ToListAsync(Ct);

    private Task<List<SplitRuleVersion>> VersionsOf(Guid ruleId) =>
        DbContext.Set<SplitRuleVersion>()
            .Where(version => version.SplitRuleId == ruleId)
            .OrderBy(version => version.StartedAt)
            .ToListAsync(Ct);

    /// <summary>
    /// A category whose rule has stood for three divisions across 2023 to 2025, which is the
    /// migrated shape in miniature.
    /// </summary>
    private async Task<(Guid CategoryId, Guid RuleId)> ACategoryWithAHistory(
        Guid groupId, Guid self, Guid other)
    {
        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            // Deliberately not an even division. An expense divided two to one cannot be
            // mistaken for one divided evenly, and "evenly, under no rule" is exactly what a
            // cleared version means -- so the two stay distinguishable in the shares.
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            DefaultSplitRuleId = rule.Id
        }, Ct);

        await Rules.SetHistory(rule.Id,
        [
            new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
            new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1)),
            new SplitRuleVersionInput(On(2025, 6), Shares(self, 2, other, 1))
        ], Ct);

        return (category.Id, rule.Id);
    }

    // ---- Reattaching the back catalogue ---------------------------------------------------

    /// <summary>
    /// The property that matters more than any other here: a reattach is a pointer moving,
    /// and the group's balances are byte-identical on the other side of it.
    /// </summary>
    /// <remarks>
    /// Not "close enough" and not "sums to zero either way": every member's paid, owed and
    /// net figure compared field by field. The splitter is not on this path and must never
    /// be, and a balance that moved would be the first sign it had got there.
    /// </remarks>
    [Fact]
    public async Task Reattaching_leaves_every_balance_in_the_group_exactly_as_it_was()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        foreach (var on in new[] { On(2023, 6), On(2024, 5), On(2025, 9), On(2026, 2) })
            await AnExpense(groupId, categoryId, 90.00m, on);

        // One with no category at all, and one somebody split by hand, so the pass has both
        // kinds of expense it must not touch in front of it.
        await AnExpense(groupId, categoryId: null, 40.00m, On(2024, 7));

        await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Cake",
            Amount = 30.00m,
            DateTime = On(2024, 8),
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = self, Amount = 30.00m }]
        }, Ct);

        var before = await Balances(groupId);

        var summary = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        var after = await Balances(groupId);

        Assert.True(summary.Changed > 0, "the fixture is meant to have something to correct");
        Assert.Equal(before, after);
    }

    /// <summary>
    /// Each expense ends up pointing at the version that was in force on the day it was
    /// spent, rather than at the one the rule happens to be on now.
    /// </summary>
    [Fact]
    public async Task Every_expense_is_pointed_at_the_version_in_force_on_its_own_date()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, ruleId) = await ACategoryWithAHistory(groupId, self, other);

        var inTwentyThree = await AnExpense(groupId, categoryId, 90.00m, On(2023, 6));
        var inTwentyFour = await AnExpense(groupId, categoryId, 90.00m, On(2024, 5));
        var inTwentySix = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        var versions = await VersionsOf(ruleId);

        await Provenance.Reattach(new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(versions[0].Id, await VersionOn(inTwentyThree.Id));
        Assert.Equal(versions[1].Id, await VersionOn(inTwentyFour.Id));
        Assert.Equal(versions[2].Id, await VersionOn(inTwentySix.Id));
    }

    /// <summary>
    /// A window is closed at its start and open at its end, so an expense recorded at the
    /// very moment a version opened belongs to that version and not to the one before it.
    /// </summary>
    [Fact]
    public async Task An_expense_on_the_boundary_belongs_to_the_version_that_opened_there()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, ruleId) = await ACategoryWithAHistory(groupId, self, other);

        var onTheBoundary = await AnExpense(groupId, categoryId, 90.00m, On(2024, 1));
        var aMomentBefore = await AnExpense(
            groupId, categoryId, 90.00m, On(2024, 1).AddSeconds(-1));

        var versions = await VersionsOf(ruleId);

        await Provenance.Reattach(new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(versions[1].Id, await VersionOn(onTheBoundary.Id));
        Assert.Equal(versions[0].Id, await VersionOn(aMomentBefore.Id));
    }

    /// <summary>
    /// An expense older than the history written for its rule has no honest answer, so it is
    /// left pointing at nothing and counted as such.
    /// </summary>
    [Fact]
    public async Task An_expense_older_than_the_history_is_left_pointing_at_nothing()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        var tooEarly = await AnExpense(groupId, categoryId, 90.00m, On(2022, 11));

        var summary = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Null(await VersionOn(tooEarly.Id));
        Assert.Equal(1, summary.LeftWithoutAVersion);
        Assert.Equal(1, Assert.Single(summary.ByRule).Uncovered);
    }

    /// <summary>
    /// An expense filed under nothing was never divided by a rule and stays that way.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_category_is_left_pointing_at_nothing()
    {
        var (groupId, self, other) = await GroupOfTwo();
        await ACategoryWithAHistory(groupId, self, other);

        var personal = await AnExpense(groupId, categoryId: null, 40.00m, On(2024, 7));

        var summary = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Null(await VersionOn(personal.Id));
        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.LeftWithoutAVersion);
        Assert.Empty(summary.ByRule);
    }

    /// <summary>
    /// The summary counts what it says it counts, per rule as well as over the group.
    /// </summary>
    [Fact]
    public async Task The_summary_counts_what_was_examined_changed_and_left_alone()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        await AnExpense(groupId, categoryId, 90.00m, On(2023, 6));
        await AnExpense(groupId, categoryId, 90.00m, On(2024, 5));
        // Already correct: created after the history was written, on a date the open version
        // covers, so the splitter pointed it at that version itself.
        await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));
        await AnExpense(groupId, categoryId: null, 40.00m, On(2024, 7));

        var summary = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(4, summary.Examined);
        Assert.Equal(2, summary.Changed);
        Assert.Equal(1, summary.LeftWithoutAVersion);

        var rule = Assert.Single(summary.ByRule);

        Assert.Equal("Groceries", rule.SplitRuleName);
        Assert.Equal(3, rule.Examined);
        Assert.Equal(2, rule.Changed);
        Assert.Equal(0, rule.Uncovered);
    }

    /// <summary>
    /// A dry run answers the same summary and saves none of it.
    /// </summary>
    [Fact]
    public async Task A_dry_run_answers_the_same_summary_and_writes_nothing()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        var old = await AnExpense(groupId, categoryId, 90.00m, On(2023, 6));
        var before = await VersionOn(old.Id);

        var dry = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId, DryRun = true }, Ct);

        Assert.True(dry.DryRun);
        Assert.Equal(1, dry.Changed);
        Assert.Equal(before, await VersionOn(old.Id));

        var real = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(dry.Changed, real.Changed);
        Assert.NotEqual(before, await VersionOn(old.Id));
    }

    /// <summary>
    /// Running it twice changes nothing the second time, which is what makes it safe to run
    /// again after the history is corrected.
    /// </summary>
    [Fact]
    public async Task Running_it_again_changes_nothing()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        await AnExpense(groupId, categoryId, 90.00m, On(2023, 6));
        await AnExpense(groupId, categoryId, 90.00m, On(2024, 5));

        await Provenance.Reattach(new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        var again = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(0, again.Changed);
    }

    /// <summary>
    /// Another group's ledger is not the caller's to rewrite, and answers as a missing group
    /// does rather than as an empty pass.
    /// </summary>
    [Fact]
    public async Task A_group_the_caller_is_not_in_is_not_found()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Provenance.Reattach(new ReattachTransactionsRequest { GroupId = Guid.NewGuid() }, Ct));

        Assert.Equal(ErrorCodes.GroupNotFound, refusal.Code);
    }

    /// <summary>
    /// A settlement was never divided by anything, so the pass does not reach for one.
    /// </summary>
    [Fact]
    public async Task A_settlement_is_not_examined()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        await AnExpense(groupId, categoryId, 90.00m, On(2024, 5));

        await GetService<ISettlementService>().RecordRepayment(groupId, new RecordRepaymentRequest
        {
            FromUserId = other,
            ToUserId = self,
            Amount = 10.00m,
            Date = On(2024, 6)
        }, Ct);

        var summary = await Provenance.Reattach(
            new ReattachTransactionsRequest { GroupId = groupId }, Ct);

        Assert.Equal(1, summary.Examined);
    }

    // ---- The per-expense override ---------------------------------------------------------

    /// <summary>
    /// Saying an expense's shares are its own clears the version and touches no amount.
    /// </summary>
    [Fact]
    public async Task Clearing_the_division_source_keeps_every_share_where_it_is()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        var expense = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        var before = (await SplitsOf(expense.Id)).Select(split => (split.UserId, split.Amount)).ToList();
        Assert.NotNull(await VersionOn(expense.Id));

        await Provenance.SetDivisionSource(expense.Id, new SetDivisionSourceRequest(null), Ct);

        Assert.Null(await VersionOn(expense.Id));
        Assert.Equal(
            before,
            (await SplitsOf(expense.Id)).Select(split => (split.UserId, split.Amount)).ToList());
    }

    /// <summary>
    /// And naming a version records it, again without touching a share -- which is the point:
    /// the amounts are already right and what was wrong was the account of where they came
    /// from.
    /// </summary>
    [Fact]
    public async Task Naming_a_version_records_it_and_keeps_every_share_where_it_is()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, ruleId) = await ACategoryWithAHistory(groupId, self, other);

        var expense = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        var before = (await SplitsOf(expense.Id)).Select(split => (split.UserId, split.Amount)).ToList();

        // The oldest version, which divides three to two rather than evenly -- so a path that
        // re-divided would be plainly visible in the shares.
        var oldest = (await VersionsOf(ruleId))[0];

        await Provenance.SetDivisionSource(
            expense.Id, new SetDivisionSourceRequest(oldest.Id), Ct);

        Assert.Equal(oldest.Id, await VersionOn(expense.Id));
        Assert.Equal(
            before,
            (await SplitsOf(expense.Id)).Select(split => (split.UserId, split.Amount)).ToList());
    }

    /// <summary>
    /// A version belonging to another group's rule describes a division this group's members
    /// cannot see, and is refused.
    /// </summary>
    [Fact]
    public async Task A_version_from_another_group_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        var expense = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        var elsewhere = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);

        var theirRule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = elsewhere.Id,
            Name = "Even",
            Definition = new EvenSplitRuleDto()
        }, Ct);

        var theirVersion = (await VersionsOf(theirRule.Id))[0];

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Provenance.SetDivisionSource(
                expense.Id, new SetDivisionSourceRequest(theirVersion.Id), Ct));

        Assert.Equal(ErrorCodes.SplitRuleVersionNotInGroup, refusal.Code);
    }

    /// <summary>
    /// A version that does not exist answers the same way a version from elsewhere does, so
    /// guessing ids teaches nobody what another group keeps.
    /// </summary>
    [Fact]
    public async Task A_version_that_does_not_exist_is_refused_the_same_way()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, _) = await ACategoryWithAHistory(groupId, self, other);

        var expense = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Provenance.SetDivisionSource(
                expense.Id, new SetDivisionSourceRequest(Guid.NewGuid()), Ct));

        Assert.Equal(ErrorCodes.SplitRuleVersionNotInGroup, refusal.Code);
    }

    [Fact]
    public async Task An_expense_the_caller_cannot_read_is_not_found()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Provenance.SetDivisionSource(Guid.NewGuid(), new SetDivisionSourceRequest(null), Ct));

        Assert.Equal(ErrorCodes.TransactionNotFound, refusal.Code);
    }

    /// <summary>
    /// What the override is actually for: an expense marked as its own record stops following
    /// its rule when the amount is corrected, and one marked as a rule's starts.
    /// </summary>
    [Fact]
    public async Task What_it_records_decides_whether_a_later_amount_edit_divides_again()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var (categoryId, ruleId) = await ACategoryWithAHistory(groupId, self, other);

        // The version in force now, which divides two to one: 90.00 is 60.00 and 30.00, and
        // 120.00 would be 80.00 and 40.00.
        var open = Assert.Single(await VersionsOf(ruleId), version => version.SupersededAt is null);

        var asARules = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));
        var itsOwn = await AnExpense(groupId, categoryId, 90.00m, On(2026, 2));

        // Cleared and then named again, so what the second half is divided by is what the
        // override wrote rather than what the create happened to leave behind.
        await Provenance.SetDivisionSource(asARules.Id, new SetDivisionSourceRequest(null), Ct);
        await Provenance.SetDivisionSource(asARules.Id, new SetDivisionSourceRequest(open.Id), Ct);

        var rulesModel = await Transactions.GetUpdateModel(asARules.Id, Ct);
        Assert.NotNull(rulesModel);
        rulesModel.Amount = 120.00m;

        await Transactions.Update(asARules.Id, rulesModel, Ct);

        // Recorded as that version's, the shares follow the amount: two to one at the new
        // total, which is the half a revert of the division-source write leaves standing.
        var divided = await SplitsOf(asARules.Id);

        Assert.Equal(80.00m, divided.Single(split => split.UserId == self).Amount);
        Assert.Equal(40.00m, divided.Single(split => split.UserId == other).Amount);

        await Provenance.SetDivisionSource(itsOwn.Id, new SetDivisionSourceRequest(null), Ct);

        var ownModel = await Transactions.GetUpdateModel(itsOwn.Id, Ct);
        Assert.NotNull(ownModel);
        ownModel.Amount = 120.00m;

        // Without a version the shares are the expense's own, and its own shares do not
        // follow an amount they were not worked out from.
        var refusal = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Transactions.Update(itsOwn.Id, ownModel, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, refusal.Code);
    }

    private async Task<List<(Guid UserId, decimal Paid, decimal Owed, decimal Balance)>> Balances(Guid groupId)
    {
        var rows = await (await GetService<IGroupService>().GetGroupNetBalance(groupId, Ct))
            .ToListAsync(Ct);

        return [.. rows
            .Select(row => (row.UserId, row.AmountPaid, row.AmountOwed, row.Balance))
            .OrderBy(row => row.UserId)];
    }
}
