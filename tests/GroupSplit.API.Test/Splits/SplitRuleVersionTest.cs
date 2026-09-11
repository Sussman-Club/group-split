using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Editing a rule adds to its history rather than overwriting it, and a transaction
/// remembers which entry in that history divided it.
/// </summary>
/// <remarks>
/// The pair is what makes an expense recalculable. The stored splits say what each person
/// owed; the version says by what. Either alone leaves a question that cannot be answered
/// later -- amounts with no rule behind them, or a rule that has since changed.
/// </remarks>
public class SplitRuleVersionTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private ITransactionService Transactions => GetService<ITransactionService>();

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

    private Task<List<SplitRuleVersion>> VersionsOf(Guid ruleId) =>
        DbContext.Set<SplitRuleVersion>()
            .Where(version => version.SplitRuleId == ruleId)
            .OrderBy(version => version.StartedAt)
            .ToListAsync(Ct);

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(Ct);

    private async Task<Guid?> VersionOn(Guid transactionId) =>
        await DbContext.Set<Data.Entities.Transaction>()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.SplitRuleVersionId)
            .FirstAsync(Ct);

    private Task<Expense> AnExpense(Guid groupId, Guid categoryId, decimal amount) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct).AsTask();

    [Fact]
    public async Task A_new_rule_starts_with_one_version_and_it_is_the_current_one()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        var version = Assert.Single(await VersionsOf(rule.Id));

        Assert.Null(version.SupersededAt);
    }

    [Fact]
    public async Task Editing_the_division_opens_a_version_and_closes_the_one_before_it()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        var versions = await VersionsOf(rule.Id);

        Assert.Equal(2, versions.Count);
        Assert.NotNull(versions[0].SupersededAt);
        Assert.Null(versions[1].SupersededAt);

        // The closed one is closed at the moment the new one opens, so there is no window in
        // which a rule says two things or nothing.
        Assert.Equal(versions[0].SupersededAt, versions[1].StartedAt);
    }

    /// <summary>
    /// The name belongs to the rule and not to what it says, so renaming is not a change of
    /// division and writes no version.
    /// </summary>
    [Fact]
    public async Task Renaming_a_rule_writes_no_version()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "The rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        Assert.Single(await VersionsOf(rule.Id));
        Assert.Equal("The rent", (await Rules.GetDetails(rule.Id, Ct)).Name);
    }

    /// <summary>
    /// Otherwise every save from a dialog that round-trips the definition would add a row,
    /// and a rule's history would stop being a list of the times it changed.
    /// </summary>
    [Fact]
    public async Task Saving_the_same_division_again_writes_no_version()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        // The same weights, listed the other way round: a rule is who it names and with what
        // weight, not the order a dictionary enumerated them in.
        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = Shares(other, 1, self, 2)
        }, Ct);

        Assert.Single(await VersionsOf(rule.Id));
    }

    /// <summary>
    /// A rule changing kind used to mean deleting a row and creating another, because a row
    /// cannot change type -- which took the old division with it. A new version is a new row
    /// whatever kind it is.
    /// </summary>
    [Fact]
    public async Task A_rule_can_change_kind_without_losing_what_it_used_to_say()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = new PercentSplitRuleDto
            {
                Percentages = new Dictionary<Guid, decimal> { [self] = 60m, [other] = 40m }
            }
        }, Ct);

        var versions = await VersionsOf(rule.Id);

        Assert.Equal(2, versions.Count);
        Assert.IsType<SharesSplitRuleVersion>(versions[0]);
        Assert.IsType<PercentSplitRuleVersion>(versions[1]);
    }

    [Fact]
    public async Task The_history_reads_newest_first_and_holds_every_division()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Rent",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        var history = await Rules.GetHistory(rule.Id, Ct);

        Assert.Equal(2, history.Versions.Count);
        Assert.Null(history.Versions[0].SupersededAt);
        Assert.NotNull(history.Versions[1].SupersededAt);

        var superseded = Assert.IsType<SharesSplitRuleDto>(history.Versions[1].Definition);

        Assert.Equal(2, superseded.Shares[self]);
    }

    [Fact]
    public async Task An_expense_records_the_version_that_divided_it()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        var ruleId = await DbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRuleId)
            .FirstAsync(Ct);

        var current = Assert.Single(await VersionsOf(ruleId!.Value));

        Assert.Equal(current.Id, await VersionOn(expense.Id));
    }

    /// <summary>
    /// The whole point of the shape: an edit to the expense is divided by the rule the
    /// expense had, not by the rule the category has now.
    /// </summary>
    /// <remarks>
    /// Nobody correcting an amount is asking to be re-billed under a division agreed after
    /// the fact. Moving the expense to another category is the edit that does ask for that,
    /// and is covered below.
    /// </remarks>
    [Fact]
    public async Task Editing_an_expense_divides_it_again_by_the_version_it_was_written_under()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);
        var writtenUnder = await VersionOn(expense.Id);

        var ruleId = await DbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRuleId)
            .FirstAsync(Ct);

        // The flat agrees to split the rent evenly from now on.
        await Rules.Update(ruleId!.Value, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        await Transactions.Update(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 120.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(120.00m, splits.Sum(split => split.Amount));

        // Two to one on the new amount, which is the old rule applied afresh -- not 60/60,
        // which is what today's rule would have said.
        Assert.Equal(80.00m, splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(40.00m, splits.Single(split => split.UserId == other).Amount);

        Assert.Equal(writtenUnder, await VersionOn(expense.Id));
    }

    [Fact]
    public async Task Filing_an_expense_under_another_category_divides_it_by_that_categorys_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rent = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));
        var food = await CreateCategory(groupId, "Food", Shares(self, 1, other, 1));

        var expense = await AnExpense(groupId, rent, 90.00m);

        await Transactions.Update(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = food
        }, ct: Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.All(splits, split => Assert.Equal(45.00m, split.Amount));

        var foodRuleId = await DbContext.Set<Category>()
            .Where(category => category.Id == food)
            .Select(category => category.DefaultSplitRuleId)
            .FirstAsync(Ct);

        var current = Assert.Single(await VersionsOf(foodRuleId!.Value));

        Assert.Equal(current.Id, await VersionOn(expense.Id));
    }

    /// <summary>
    /// Shares somebody typed in are their own record. Claiming a rule behind them would make
    /// the next edit re-divide by a division nobody chose.
    /// </summary>
    [Fact]
    public async Task An_expense_split_by_hand_names_no_version()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 10.00m },
                new SplitInput { UserId = other, Amount = 80.00m }
            ]
        }, Ct);

        Assert.Null(await VersionOn(expense.Id));
    }

    /// <summary>
    /// An expense that had a rule and is then split by hand stops having one, so a later
    /// edit does not quietly go back to the rule.
    /// </summary>
    [Fact]
    public async Task Stating_the_shares_on_an_expense_gives_up_the_version_it_had()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        Assert.NotNull(await VersionOn(expense.Id));

        await Transactions.Update(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 10.00m },
                new SplitInput { UserId = other, Amount = 80.00m }
            ]
        }, Ct);

        Assert.Null(await VersionOn(expense.Id));
    }

    /// <summary>
    /// Editing anything but the division leaves the version that produced it in place.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ITransactionService.GetUpdateModel"/>, because that is what the
    /// endpoint hands the service and the whole difficulty is in there: since 47c6904 the
    /// model carries the expense's stored shares, so a patch that only renames it still
    /// reaches the splitter with a division stated. Taking that as "somebody typed these"
    /// threw away the version on the first edit of a name -- the one record of which rule
    /// divided the expense, and the record this whole change exists to keep.
    /// </remarks>
    [Fact]
    public async Task Renaming_an_expense_keeps_the_version_that_divided_it()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        var divided = await VersionOn(expense.Id);
        Assert.NotNull(divided);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(edit);
        edit.Name = "Rent, March";

        await Transactions.Update(expense.Id, edit, Ct);

        Assert.Equal(divided, await VersionOn(expense.Id));

        // And the shares are the ones it had, which is the other half of the contract: a
        // rename moves no money.
        var splits = await SplitsOf(expense.Id);
        Assert.Equal(60.00m, splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(30.00m, splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// And the preview of that rename names the rule, and says the rule has moved on since.
    /// </summary>
    /// <remarks>
    /// The two things the dialog and the CLI print above a division, and both were blank on
    /// every in-group edit: the preview carried the stored shares forward, the splitter read
    /// stated shares as provenance given up, and so nothing was left to name. The sentence
    /// "the rule has changed since this was recorded" could not appear at all, which is
    /// exactly when somebody needs it -- they have just edited the rule and the numbers do
    /// not match it.
    /// </remarks>
    [Fact]
    public async Task Previewing_a_rename_names_the_rule_that_divided_it_and_says_it_has_changed_since()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        var ruleId = await DbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRuleId)
            .FirstAsync(Ct);

        await Rules.Update(ruleId!.Value, new UpdateSplitRuleRequest
        {
            Name = "Rent",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(edit);
        edit.Name = "Rent, March";

        var preview = await Transactions.PreviewUpdate(expense.Id, edit, ct: Ct);

        Assert.Equal("Rent", preview.RuleName);
        Assert.NotNull(preview.RuleSupersededAt);

        // Two to one, which is what the superseded version says. The rule reads one to one
        // now, and an expense recorded under the old one is not re-billed under the new.
        Assert.Equal(60.00m, preview.Splits.Single(split => split.UserId == self).Amount);
    }

    /// <summary>
    /// A personal expense can be previewed, and the answer is the save's: the whole of it
    /// against the one person there is.
    /// </summary>
    /// <remarks>
    /// It could not be. The preview carried the stored shares forward whenever the group had
    /// not changed, and "not changed" was true of an expense that has no group on either
    /// side -- so a personal expense arrived at the splitter with its single share stated,
    /// and the splitter refuses any stated share on an expense nobody shares. The save of
    /// the very same edit succeeded, because GetUpdateModel hands a group-less transaction
    /// no shares at all, which made `groupsplit tx update &lt;id&gt; --name X --preview` fail
    /// on an edit that was about to work.
    /// </remarks>
    [Fact]
    public async Task Previewing_an_edit_to_a_personal_expense_answers_the_way_the_save_does()
    {
        var self = GetService<ICurrentUser>().User.Id;

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Haircut",
            Amount = 24.00m,
            DateTime = DateTimeOffset.UtcNow
        }, Ct);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(edit);
        edit.Name = "Haircut, March";

        var preview = await Transactions.PreviewUpdate(expense.Id, edit, ct: Ct);

        var share = Assert.Single(preview.Splits);
        Assert.Equal(self, share.UserId);
        Assert.Equal(24.00m, share.Amount);
        Assert.Null(preview.RuleName);

        // And the save it was previewing does what it said.
        await Transactions.Update(expense.Id, edit, Ct);

        var stored = Assert.Single(await SplitsOf(expense.Id));
        Assert.Equal(24.00m, stored.Amount);
    }

    /// <summary>
    /// A version an expense points at is the only record of which division produced its
    /// amounts, so the rule it belongs to is history and not a row to tidy away.
    /// </summary>
    [Fact]
    public async Task A_rule_an_expense_was_divided_by_cannot_be_deleted()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        await AnExpense(groupId, categoryId, 90.00m);

        var ruleId = await DbContext.Set<Category>()
            .Where(category => category.Id == categoryId)
            .Select(category => category.DefaultSplitRuleId)
            .FirstAsync(Ct);

        // The category has to stop pointing at it first, or the refusal below is the other
        // one -- which is a different reason and would pass without testing this one.
        await GetService<ICategoryService>().Update(categoryId, new UpdateCategoryRequest
        {
            Name = "Rent",
            DefaultSplitRuleId = null
        }, Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(
            () => Rules.Delete(ruleId!.Value, Ct));

        Assert.Contains("history", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The preview reproduces the save, refusal and all.
    /// </summary>
    /// <remarks>
    /// Changing the amount alone is refused, because the endpoint carries the expense's
    /// existing shares into the patched model and they no longer sum to the new total --
    /// see <c>TransactionSplitPatchTest</c>, where that is the stated contract. The preview
    /// exists to meet that while there is still a flag to add, so it has to refuse in the
    /// same place rather than quietly dividing afresh and promising a save that will not
    /// happen.
    /// </remarks>
    /// <summary>
    /// Changing the amount alone on an expense its rule divided divides it again at the new
    /// amount, and the preview says so because the save does.
    /// </summary>
    /// <remarks>
    /// It used to be refused, by both, with <c>SPLITS_DO_NOT_SUM_TO_AMOUNT</c>: the stored
    /// shares were carried onto the new total and no longer added up to it. That refusal was
    /// right while nothing could tell a division a rule produced from one a person typed,
    /// and it made correcting a receipt a two-step operation on every expense in the group.
    /// Re-running the rule against the stored amount answers the question the refusal was
    /// standing in for, so shares nobody chose follow the amount they were worked out from.
    /// <para>
    /// Only those. <see cref="Previewing_an_amount_change_alone_on_a_hand_split_expense_is_refused"/>
    /// is the other half, and it is where the refusal still lives.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Previewing_an_amount_change_alone_divides_it_again_at_the_new_amount()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 120.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = categoryId
        }, ct: Ct);

        Assert.Equal(80.00m, preview.Splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(40.00m, preview.Splits.Single(split => split.UserId == other).Amount);
        Assert.Equal("Rent", preview.RuleName);
    }

    /// <summary>
    /// Shares somebody typed are their own record, so the amount cannot move out from under
    /// them: the preview refuses exactly as the save does.
    /// </summary>
    [Fact]
    public async Task Previewing_an_amount_change_alone_on_a_hand_split_expense_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 70.00m },
                new SplitInput { UserId = other, Amount = 20.00m }
            ]
        }, Ct);

        var refused = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
            {
                Name = "Rent",
                Amount = 120.00m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = self,
                GroupId = groupId,
                CategoryId = categoryId
            }, ct: Ct));

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, refused.Code);
    }

    /// <summary>
    /// Stating the shares previews them, which is the edit the refusal above points at.
    /// </summary>
    [Fact]
    public async Task Previewing_an_amount_change_with_the_shares_stated_shows_them()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var expense = await AnExpense(groupId, categoryId, 90.00m);

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 120.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 80.00m },
                new SplitInput { UserId = other, Amount = 40.00m }
            ]
        }, ct: Ct);

        Assert.Equal(80.00m, preview.Splits.Single(split => split.UserId == self).Amount);
        Assert.Null(preview.RuleName);
    }

    /// <summary>
    /// Filing an expense a rule divided under a different category divides it again by the
    /// new category's rule, and the preview shows that.
    /// </summary>
    /// <remarks>
    /// The behaviour the model was reshaped for, and for a while not the behaviour there
    /// was: the endpoint carries the existing shares into any patch that does not mention
    /// them, so a change of category used to leave the old rule's numbers in place under a
    /// category that divides some other way -- a rule deciding nothing on the one edit that
    /// is entirely about which rule applies. Re-running the division answers whether the
    /// shares were the rule's to move, and here they were.
    /// <para>
    /// Shares somebody typed are not, which is the whole distinction:
    /// <see cref="Previewing_a_move_between_categories_keeps_shares_somebody_typed"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Previewing_a_move_between_categories_divides_it_by_the_new_category()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rent = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));
        var food = await CreateCategory(groupId, "Food", Shares(self, 1, other, 1));

        var expense = await AnExpense(groupId, rent, 90.00m);

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = food
        }, ct: Ct);

        // Evenly, which is Food's rule, rather than the two to one it arrived holding.
        Assert.Equal(45.00m, preview.Splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(45.00m, preview.Splits.Single(split => split.UserId == other).Amount);
        Assert.Equal("Food", preview.RuleName);
    }

    /// <summary>
    /// The same move on an expense whose shares somebody typed leaves them exactly as they
    /// are, category or no category.
    /// </summary>
    [Fact]
    public async Task Previewing_a_move_between_categories_keeps_shares_somebody_typed()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rent = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));
        var food = await CreateCategory(groupId, "Food", Shares(self, 1, other, 1));

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = rent,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 80.00m },
                new SplitInput { UserId = other, Amount = 10.00m }
            ]
        }, Ct);

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            GroupId = groupId,
            CategoryId = food
        }, ct: Ct);

        Assert.Equal(80.00m, preview.Splits.Single(split => split.UserId == self).Amount);
        Assert.Equal(10.00m, preview.Splits.Single(split => split.UserId == other).Amount);
        Assert.Null(preview.RuleName);
    }

    /// <summary>
    /// Shares somebody typed are not the category's rule, and the preview used to say they
    /// were -- it read the name off the category rather than off the division that actually
    /// happened.
    /// </summary>
    [Fact]
    public async Task A_preview_of_stated_shares_names_no_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares(self, 2, other, 1));

        var preview = await Transactions.Preview(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = 90.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = self, Amount = 10.00m },
                new SplitInput { UserId = other, Amount = 80.00m }
            ]
        }, Ct);

        Assert.Null(preview.RuleName);
        Assert.Equal(10.00m, preview.Splits.Single(split => split.UserId == self).Amount);
    }

    /// <summary>
    /// Correcting who paid on an expense under a payer rule moves the whole share onto the
    /// person who actually paid.
    /// </summary>
    /// <remarks>
    /// The case that showed the old reading of silence was not merely cautious but wrong.
    /// "Whoever paid owes all of it" is a rule whose answer is a function of the payer, so
    /// an expense recorded against the wrong person kept a share saying the wrong person
    /// owed it -- a debt between two people, standing in the group's balances, that the very
    /// edit meant to correct it left untouched.
    /// </remarks>
    [Fact]
    public async Task Correcting_who_paid_moves_the_whole_share_under_a_payer_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Their own", new PayerSplitRuleDto());

        var expense = await AnExpense(groupId, categoryId, 40.00m);

        var mine = Assert.Single(await SplitsOf(expense.Id));
        Assert.Equal(self, mine.UserId);

        var model = await Transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(model);

        model.PaidByUserId = other;

        await Transactions.Update(expense.Id, model, Ct);

        var theirs = Assert.Single(await SplitsOf(expense.Id));

        Assert.Equal(other, theirs.UserId);
        Assert.Equal(40.00m, theirs.Amount);
    }

    /// <summary>
    /// And the preview of that same correction agrees with it, because the two read silence
    /// the same way.
    /// </summary>
    [Fact]
    public async Task Previewing_a_corrected_payer_agrees_with_the_save()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Their own", new PayerSplitRuleDto());

        var expense = await AnExpense(groupId, categoryId, 40.00m);

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = 40.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = other,
            GroupId = groupId,
            CategoryId = categoryId
        }, ct: Ct);

        var share = Assert.Single(preview.Splits);

        Assert.Equal(other, share.UserId);
        Assert.Equal(40.00m, share.Amount);
    }

    [Fact]
    public async Task A_rule_nothing_has_been_recorded_under_deletes_with_its_versions()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Unused",
            Definition = Shares(self, 2, other, 1)
        }, Ct);

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Unused",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        await Rules.Delete(rule.Id, Ct);

        Assert.Empty(await VersionsOf(rule.Id));
    }
}
