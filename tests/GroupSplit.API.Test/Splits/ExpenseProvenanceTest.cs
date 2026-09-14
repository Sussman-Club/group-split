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
/// It exists because a migration had to guess. A ledger imported from a workbook that never
/// knew about versions arrived with every categorised expense pointing at its category's
/// only version, so an expense from March 2023 claims to have been divided by a ratio the
/// group agreed in 2026. Stating what actually divided one is how that is put right, one
/// expense at a time and by somebody who knows.
/// <para>
/// There was a pass over a whole group beside it, resolving each expense through its
/// category's rule and the dates on the rows. It is gone: a category that has been
/// re-pointed since makes every answer it gives a rule the expense was never divided by, and
/// nothing in the database says which rule a category named three years ago.
/// </para>
/// <para>
/// The property every test here is really about: a pointer is not a division, and correcting
/// one may not move a single cent.
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
