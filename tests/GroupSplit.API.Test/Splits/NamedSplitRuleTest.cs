using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// An expense that names the rule it divides by, whatever its category says.
/// </summary>
/// <remarks>
/// The case the category cannot answer: the weekly shop is even, this one is Ana's gym.
/// Stating the shares by hand would divide it correctly today and cost the expense its
/// arithmetic -- correcting the amount afterwards is refused, because shares that summed to
/// the old total do not sum to the new one. Naming a rule keeps it a division somebody can
/// go on editing.
/// </remarks>
public class NamedSplitRuleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ITransactionService Transactions => GetService<ITransactionService>();

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private ICategoryService Categories => GetService<ICategoryService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var self = Self;

        var group = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    /// <summary>The rule the group holds for one of its members: all of it is for them.</summary>
    private async Task<SplitRule> RuleFor(Guid groupId, Guid userId)
    {
        var group = await DbContext.Set<Data.Entities.Group>()
            .FirstAsync(candidate => candidate.Id == groupId, Ct);

        var user = await DbContext.Set<Data.Entities.User>().FirstAsync(candidate => candidate.Id == userId, Ct);

        return await GetService<IMemberSplitRules>().EnsureFor(group, user, Ct);
    }

    private Task<Expense> AnExpense(
        Guid groupId, decimal amount, Guid? splitRuleId = null, Guid? categoryId = null,
        IReadOnlyList<SplitInput>? splits = null) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "The gym",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            SplitRuleId = splitRuleId,
            Splits = splits
        }, Ct).AsTask();

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(Ct);

    private static decimal Owed(IEnumerable<TransactionSplit> splits, Guid userId) =>
        splits.Where(split => split.UserId == userId).Sum(split => split.Amount);

    /// <summary>
    /// Which rule an expense is divided by, which is a question about the version it holds:
    /// nothing on the transaction names a rule.
    /// </summary>
    private async Task<Guid?> RuleBehind(Guid transactionId)
    {
        var version = await DbContext.Set<Data.Entities.Transaction>()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.SplitRuleVersionId)
            .FirstAsync(Ct);

        if (version is null)
            return null;

        return await DbContext.Set<SplitRuleVersion>()
            .Where(candidate => candidate.Id == version)
            .Select(candidate => (Guid?)candidate.SplitRuleId)
            .FirstAsync(Ct);
    }

    private async Task<Guid> ACategory(Guid groupId, string name, Guid? ruleId = null) =>
        (await Categories.Create(new CreateCategoryRequest
        {
            GroupId = groupId,
            Name = name,
            DefaultSplitRuleId = ruleId
        }, Ct)).Id;

    [Fact]
    public async Task Naming_a_rule_divides_the_whole_expense_by_it()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(60.00m, Owed(splits, other));
        Assert.Equal(0m, Owed(splits, self));
    }

    /// <summary>
    /// The category is not consulted at all when the expense named a rule. Filing is what
    /// the money was for; the rule is who it was for.
    /// </summary>
    [Fact]
    public async Task The_rule_it_names_beats_the_one_its_category_defaults_to()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var evenly = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Down the middle",
            Definition = new EvenSplitRuleDto()
        }, Ct);

        var categoryId = await ACategory(groupId, "Health", evenly.Id);
        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id, categoryId: categoryId);

        Assert.Equal(60.00m, Owed(await SplitsOf(expense.Id), other));
    }

    /// <summary>
    /// The whole reason this is a rule rather than a set of typed-in shares: the expense
    /// stays divisible. Hand-typed shares that summed to 60 are refused against a new total.
    /// </summary>
    [Fact]
    public async Task Correcting_the_amount_divides_it_again_by_the_same_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        // The edit says nothing about the division -- there is nothing on an expense naming a
        // rule to read back, and nothing to send. What it holds is the division itself.
        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);

        Assert.NotNull(edit);
        Assert.Null(edit.SplitRuleId);

        edit.Amount = 75.00m;

        await Transactions.Update(expense.Id, edit, Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(75.00m, Owed(splits, other));
        Assert.Equal(0m, Owed(splits, self));
        Assert.Equal(theirs.Id, await RuleBehind(expense.Id));
    }

    /// <summary>
    /// Which is the fact a stored version alone could not carry: the expense goes on being
    /// divided by the rule it named even after it is filed somewhere that divides otherwise.
    /// </summary>
    [Fact]
    public async Task Filing_it_under_a_category_that_divides_differently_does_not_take_the_rule_away()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var evenly = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Down the middle",
            Definition = new EvenSplitRuleDto()
        }, Ct);

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.CategoryId = await ACategory(groupId, "Health", evenly.Id);

        await Transactions.Update(expense.Id, edit, Ct);

        Assert.Equal(60.00m, Owed(await SplitsOf(expense.Id), other));
    }

    /// <summary>
    /// And the way out of it: stop naming a rule, and the expense divides the way whatever
    /// it is filed under does.
    /// </summary>
    [Fact]
    public async Task Giving_up_the_rule_hands_the_expense_back_to_its_category()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        // Asking outright, which is what the dialog's "Automatically" and the CLI's
        // --redivide send: an expense divided by a rule somebody named has nothing else to
        // say "never mind who it was for" with.
        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.Splits = null;

        await Transactions.Update(expense.Id, edit, Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(30.00m, Owed(splits, other));
        Assert.Equal(30.00m, Owed(splits, self));
    }

    /// <summary>
    /// An edit that moves nothing the division was worked out from moves no money -- and
    /// leaves the expense holding the division it held. The same guarantee every other edit
    /// has.
    /// </summary>
    [Fact]
    public async Task Renaming_it_leaves_the_division_alone()
    {
        var (groupId, _, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.Name = "Gym, January";

        await Transactions.Update(expense.Id, edit, Ct);

        Assert.Equal(theirs.Id, await RuleBehind(expense.Id));
        Assert.Equal(60.00m, Owed(await SplitsOf(expense.Id), other));
    }

    /// <summary>
    /// The version is recorded as it is for any other rule, so the expense can say which
    /// division produced its shares.
    /// </summary>
    /// <summary>
    /// And what it records is the version, which is the whole of what it stores: naming a
    /// rule is how a person says it, and the division that rule stands for is the answer.
    /// </summary>
    [Fact]
    public async Task It_records_the_version_that_divided_it()
    {
        var (groupId, _, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        Assert.Equal(theirs.Id, await RuleBehind(expense.Id));
    }

    /// <summary>
    /// Two answers to one question. A request carrying both has not said which it means, so
    /// it is refused rather than resolved by precedence.
    /// </summary>
    [Fact]
    public async Task Naming_a_rule_and_stating_the_shares_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var refusal = await Assert.ThrowsAsync<ValidationException>(() => AnExpense(
            groupId, 60.00m, splitRuleId: theirs.Id,
            splits:
            [
                new SplitInput { UserId = self, Amount = 30.00m },
                new SplitInput { UserId = other, Amount = 30.00m }
            ]));

        Assert.Equal(ErrorCodes.SplitsInvalid, refusal.Code);
    }

    [Fact]
    public async Task A_rule_from_another_group_is_refused()
    {
        var (groupId, _, _) = await GroupOfTwo();

        var elsewhere = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);

        var theirs = await RuleFor(elsewhere.Id, Self);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => AnExpense(groupId, 60.00m, splitRuleId: theirs.Id));

        Assert.Equal(ErrorCodes.SplitRuleNotInGroup, refusal.Code);
    }

    /// <summary>
    /// Says the same thing whether the rule exists or not, so guessing ids tells a caller
    /// nothing about another group's rules.
    /// </summary>
    [Fact]
    public async Task A_rule_that_does_not_exist_is_refused_the_same_way()
    {
        var (groupId, _, _) = await GroupOfTwo();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => AnExpense(groupId, 60.00m, splitRuleId: Guid.NewGuid()));

        Assert.Equal(ErrorCodes.SplitRuleNotInGroup, refusal.Code);
    }

    [Fact]
    public async Task A_personal_expense_has_nothing_for_a_rule_to_divide()
    {
        var (groupId, _, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Create(
            new CreateTransactionRequest
            {
                Name = "The gym",
                Amount = 60.00m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = null,
                SplitRuleId = theirs.Id
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitRuleNotInGroup, refusal.Code);
    }

    /// <summary>
    /// Typing the shares out is somebody taking the division over, and it takes the rule
    /// with it -- otherwise the next edit to the amount would divide by a rule whose answer
    /// the person had already overruled, overwriting the very amounts they typed.
    /// </summary>
    [Fact]
    public async Task Stating_the_shares_afterwards_gives_up_the_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.Splits =
        [
            new SplitInput { UserId = self, Amount = 20.00m },
            new SplitInput { UserId = other, Amount = 40.00m }
        ];

        await Transactions.Update(expense.Id, edit, Ct);

        var stored = await DbContext.Set<Data.Entities.Transaction>()
            .FirstAsync(transaction => transaction.Id == expense.Id, Ct);

        Assert.Null(stored.SplitRuleVersionId);
        Assert.Equal(40.00m, Owed(await SplitsOf(expense.Id), other));
    }

    /// <summary>
    /// A rule belongs to one group, so moving the expense somewhere else leaves it behind --
    /// as the shares are left behind, and for the same reason. The patch model carries every
    /// stored value forward, so without this a move would be refused over a field the caller
    /// never mentioned.
    /// </summary>
    [Fact]
    public async Task Moving_it_to_another_group_gives_up_the_rule_it_named()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var expense = await AnExpense(groupId, 60.00m, splitRuleId: theirs.Id);

        var elsewhere = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.GroupId = elsewhere.Id;
        edit.PaidByUserId = self;

        await Transactions.Update(expense.Id, edit, Ct);

        var stored = await DbContext.Set<Data.Entities.Transaction>()
            .FirstAsync(transaction => transaction.Id == expense.Id, Ct);

        Assert.Null(stored.SplitRuleVersionId);

        // And the destination divides it, which for a group of one is all of it on them.
        Assert.Equal(60.00m, Owed(await SplitsOf(expense.Id), self));
    }

    /// <summary>
    /// Asking to divide it again keeps the version on every other kind of division, which is
    /// what it has always done: an expense corrected months later is not re-billed under a
    /// rule agreed since. The ask means something different only where the division names a
    /// person, because there the category has never had a say.
    /// </summary>
    [Fact]
    public async Task Asking_again_still_uses_the_version_an_ordinary_expense_was_written_under()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var shares = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Two to one",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [self] = 2, [other] = 1 }
            }
        }, Ct);

        var categoryId = await ACategory(groupId, "Rent", shares.Id);

        var expense = await AnExpense(groupId, 90.00m, categoryId: categoryId);

        // The rule moves on, and the expense does not: it holds the version it was written
        // under, and asking for its division again reaches that one.
        await Rules.Update(shares.Id, new UpdateSplitRuleRequest
        {
            Name = "Two to one",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [self] = 1, [other] = 1 }
            }
        }, Ct);

        var edit = await Transactions.GetUpdateModel(expense.Id, Ct);
        edit!.Splits = null;

        await Transactions.Update(expense.Id, edit, Ct);

        Assert.Equal(60.00m, Owed(await SplitsOf(expense.Id), self));
    }

    /// <summary>
    /// The preview is the same call the save makes, and this is the one it has to agree
    /// with: a dialog showing the category's division under a control saying "all for Ana"
    /// would be wrong before anybody pressed anything.
    /// </summary>
    [Fact]
    public async Task The_preview_shows_what_naming_a_rule_would_come_to()
    {
        var (groupId, self, other) = await GroupOfTwo();

        var theirs = await RuleFor(groupId, other);

        var preview = await Transactions.Preview(new CreateTransactionRequest
        {
            Name = "The gym",
            Amount = 60.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            SplitRuleId = theirs.Id
        }, Ct);

        var only = Assert.Single(preview.Splits);

        Assert.Equal(other, only.UserId);
        Assert.Equal(60.00m, only.Amount);
        Assert.Equal(theirs.Name, preview.RuleName);
    }
}
