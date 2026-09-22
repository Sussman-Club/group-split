using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// An expense needs a group, an amount and a payer, and nothing else. A category is
/// optional: it says what the expense was for and may name a rule to divide by, and
/// without one the expense divides evenly between the group's members. That is the whole
/// of what a group with no rules used to be unable to do -- four error codes explained the
/// refusal, and the refusal went with the model that needed it.
/// </summary>
public class TransactionWithoutACategoryTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CreateTransactionRequest Request(Guid? groupId = null, Guid? categoryId = null) =>
        new()
        {
            Name = "Dinner",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        };

    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo(string name)
    {
        var groups = GetService<IGroupService>();
        var self = GetService<ICurrentUser>().User.Id;

        var group = await groups.CreateGroup(new CreateGroupRequest { Name = name },
            TestContext.Current.CancellationToken);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// The case that used to be refused. A group nobody has set up a category for records
    /// an expense like any other, and divides it between everyone in it.
    /// </summary>
    [Fact]
    public async Task A_group_with_no_categories_records_an_expense_evenly_split()
    {
        var (groupId, self, other) = await GroupOfTwo("No categories yet");

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: groupId), TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(groupId, created.GroupId);
        Assert.Null(created.CategoryId);
        Assert.Equal(2, splits.Count);
        Assert.Equal(20m, splits.Sum(split => split.Amount));
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 10m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 10m);
    }

    /// <summary>
    /// Even is now a real built-in rule, not an invisible fallback. The group chooses it by
    /// default and the expense records the version that produced its shares.
    /// </summary>
    [Fact]
    public async Task A_new_groups_uncategorized_default_is_its_built_in_even_rule()
    {
        var (groupId, self, other) = await GroupOfTwo("Built-in defaults");

        var group = await DbContext.Set<GroupSplit.Data.Entities.Group>()
            .Include(candidate => candidate.DefaultSplitRule)
            .ThenInclude(rule => rule!.Versions)
            .SingleAsync(candidate => candidate.Id == groupId, TestContext.Current.CancellationToken);

        var even = Assert.IsType<EvenSplitRuleVersion>(Assert.Single(group.DefaultSplitRule!.Versions));
        Assert.True(group.DefaultSplitRule.BuiltIn);

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: groupId), TestContext.Current.CancellationToken);

        Assert.Equal(even.Id, created.SplitRuleVersionId);
        var splits = await SplitsOf(created.Id);
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 10m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 10m);
    }

    /// <summary>A group-level choice changes future uncategorized expenses without touching categories.</summary>
    [Fact]
    public async Task An_uncategorized_expense_uses_the_groups_selected_default()
    {
        var (groupId, self, other) = await GroupOfTwo("Default override");

        var allForMe = await DbContext.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .SingleAsync(rule => rule.Group.Id == groupId && rule.BuiltIn &&
                rule.Versions.OfType<SoleSplitRuleVersion>().Any(version => version.UserId == self),
                TestContext.Current.CancellationToken);

        await GetService<IGroupService>().UpdateGroup(groupId, new CreateGroupRequest
        {
            Name = "Default override",
            DefaultSplitRuleId = allForMe.Id
        }, TestContext.Current.CancellationToken);

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: groupId), TestContext.Current.CancellationToken);

        Assert.Equal(Assert.Single(allForMe.Versions).Id, created.SplitRuleVersionId);
        var splits = await SplitsOf(created.Id);
        Assert.Single(splits);
        Assert.Equal(self, splits[0].UserId);
        Assert.Equal(20m, splits[0].Amount);
        Assert.DoesNotContain(splits, split => split.UserId == other);
    }

    /// <summary>An explicit built-in rule wins over the group default for this one expense.</summary>
    [Fact]
    public async Task An_expense_can_override_the_group_default_back_to_even()
    {
        var (groupId, self, other) = await GroupOfTwo("Expense override");

        var rules = await DbContext.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .Where(rule => rule.Group.Id == groupId && rule.BuiltIn)
            .ToListAsync(TestContext.Current.CancellationToken);
        var allForMe = rules.Single(rule => rule.Versions.Single() is SoleSplitRuleVersion sole && sole.UserId == self);
        var even = rules.Single(rule => rule.Versions.Single() is EvenSplitRuleVersion);

        await GetService<IGroupService>().UpdateGroup(groupId, new CreateGroupRequest
        {
            Name = "Expense override",
            DefaultSplitRuleId = allForMe.Id
        }, TestContext.Current.CancellationToken);

        var request = Request(groupId: groupId);
        request.SplitRuleId = even.Id;
        var created = await GetService<ITransactionService>()
            .Create(request, TestContext.Current.CancellationToken);

        Assert.Equal(Assert.Single(even.Versions).Id, created.SplitRuleVersionId);
        var splits = await SplitsOf(created.Id);
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 10m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 10m);
    }

    /// <summary>
    /// The failure the old guard existed to prevent still must not happen: naming a group
    /// files the expense in that group, never in the caller's personal one.
    /// </summary>
    [Fact]
    public async Task Such_an_expense_lands_in_the_group_named_and_not_the_personal_one()
    {
        var (groupId, _, _) = await GroupOfTwo("Ruleless");

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: groupId), TestContext.Current.CancellationToken);

        Assert.Equal(groupId, created.GroupId);
    }

    /// <summary>
    /// A group that does have categories gives the same answer when none is picked: the
    /// caller filed the expense under nothing, which is allowed, and it divides evenly
    /// rather than by the rule of a category it was not filed under.
    /// </summary>
    [Fact]
    public async Task Leaving_the_category_unpicked_in_a_group_that_has_them_is_allowed()
    {
        var (groupId, self, _) = await GroupOfTwo("Has categories");

        await CreateCategory(groupId, "Groceries", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [self] = 100m }
        });

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: groupId), TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Null(created.CategoryId);
        Assert.Equal(2, splits.Count);
        Assert.All(splits, split => Assert.Equal(10m, split.Amount));
    }

    /// <summary>
    /// A category that names a rule divides by it. The category is the label the expense
    /// was filed under; the rule is what that label pre-fills.
    /// </summary>
    [Fact]
    public async Task A_named_category_files_the_expense_and_divides_by_its_rule()
    {
        var (groupId, self, other) = await GroupOfTwo("Trip");

        var categoryId = await CreateCategory(groupId, "Food", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [self] = 25m, [other] = 75m }
        });

        var created = await GetService<ITransactionService>().Create(
            Request(groupId: groupId, categoryId: categoryId), TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(categoryId, created.CategoryId);
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 5m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 15m);
    }

    /// <summary>
    /// Moving the group's default does not re-bill the expenses recorded under the old one.
    /// </summary>
    /// <remarks>
    /// The fallback answers for an expense that has never been divided by a rule. An expense
    /// already holding a version keeps it, so correcting an amount a year later divides it
    /// by what divided it at the time -- the same promise an expense under a category has,
    /// one level up. Without this, one edit to any old uncategorized expense would quietly
    /// move every member's balance the moment somebody changed the group's default.
    /// </remarks>
    [Fact]
    public async Task Changing_the_group_default_does_not_re_divide_what_it_already_divided()
    {
        var (groupId, self, other) = await GroupOfTwo("Default moved later");
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(
            Request(groupId: groupId), TestContext.Current.CancellationToken);

        var allForMe = await DbContext.Set<SplitRule>()
            .SingleAsync(rule => rule.Group.Id == groupId && rule.BuiltIn &&
                rule.Versions.OfType<SoleSplitRuleVersion>().Any(version => version.UserId == self),
                TestContext.Current.CancellationToken);

        await GetService<IGroupService>().UpdateGroup(groupId, new CreateGroupRequest
        {
            Name = "Default moved later",
            DefaultSplitRuleId = allForMe.Id
        }, TestContext.Current.CancellationToken);

        // The amount is corrected, and nothing else. Nobody is asking to be re-billed.
        var update = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        update!.Amount = 30m;
        update.Splits = null;
        await transactions.Update(created.Id, update, TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 15m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 15m);
    }

    /// <summary>
    /// The one rule a group cannot fall back on. It divides by the lines of a receipt, and
    /// an expense with no receipt has nothing for it to read -- so a group holding it as its
    /// default could record no ordinary expense at all.
    /// </summary>
    [Fact]
    public async Task The_bill_rule_is_refused_as_a_groups_default()
    {
        var (groupId, _, _) = await GroupOfTwo("Bill as default");

        var billRule = await DbContext.Set<SplitRule>()
            .SingleAsync(rule => rule.Group.Id == groupId &&
                rule.Versions.OfType<ItemizedSplitRuleVersion>().Any(),
                TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            GetService<IGroupService>().UpdateGroup(groupId, new CreateGroupRequest
            {
                Name = "Bill as default",
                DefaultSplitRuleId = billRule.Id
            }, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(ErrorCodes.SplitRuleInvalid, refusal.Code);
    }

    /// <summary>
    /// Clearing a category is a real update, not an absent field: the expense is persisted
    /// uncategorized, reloads that way, and is re-divided with the group's fallback.
    /// </summary>
    [Fact]
    public async Task Clearing_a_category_persists_an_uncategorized_expense()
    {
        var (groupId, self, other) = await GroupOfTwo("Clear category");
        var categoryId = await CreateCategory(groupId, "Food", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [self] = 100m }
        });
        var transactions = GetService<ITransactionService>();
        var created = await transactions.Create(
            Request(groupId: groupId, categoryId: categoryId), TestContext.Current.CancellationToken);

        var update = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        update!.CategoryId = null;
        await transactions.Update(created.Id, update, TestContext.Current.CancellationToken);

        var reloaded = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);
        Assert.Null(reloaded!.CategoryId);

        var splits = await SplitsOf(created.Id);
        Assert.Contains(splits, split => split.UserId == self && split.Amount == 10m);
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 10m);
    }

    /// <summary>
    /// A category belongs to a group. Filing an expense in one group under another group's
    /// label would put it under a name the members cannot see, so it is refused the way a
    /// missing category is: whether it exists is not the caller's to learn.
    /// </summary>
    [Fact]
    public async Task A_category_from_another_group_is_refused()
    {
        var (home, self, _) = await GroupOfTwo("Home");
        var (trip, _, _) = await GroupOfTwo("Trip");

        var tripCategory = await CreateCategory(trip, "Lodging", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [self] = 100m }
        });

        var failure = await Assert.ThrowsAsync<NotFoundException>(() =>
            GetService<ITransactionService>()
                .Create(Request(groupId: home, categoryId: tripCategory), TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(ErrorCodes.CategoryNotFound, failure.Code);
    }

    /// <summary>
    /// No group, no category: a personal expense, which is now literally an expense with no
    /// group rather than one filed into a hidden group of one.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_group_at_all_is_a_personal_one()
    {
        var created = await GetService<ITransactionService>()
            .Create(Request(), TestContext.Current.CancellationToken);

        var split = Assert.Single(await SplitsOf(created.Id));

        Assert.Equal("Dinner", created.Name);
        Assert.Null(created.GroupId);
        Assert.Equal(20m, split.Amount);
        Assert.Equal(GetService<ICurrentUser>().User.Id, split.UserId);
    }

    /// <summary>
    /// And it is still the caller's own: the listing that answers "everything I have paid"
    /// has to include the expenses that belong to no group, or they would be recorded and
    /// then invisible.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_is_still_in_the_callers_listing()
    {
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(Request(), TestContext.Current.CancellationToken);

        var listed = await (await transactions.List(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(listed, expense => expense.Id == created.Id);
    }
}
