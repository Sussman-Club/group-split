using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The read and update side of <see cref="TransactionService"/>: the per-member split a
/// transaction resolves to, the model the edit form loads, and the checks an update has
/// to pass. The split arithmetic is the interesting part — it truncates each share and
/// gives the remainder to whoever paid, so the parts always add back up to the bill.
/// </summary>
public class TransactionDetailsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var groupService = GetService<IGroupService>();
        var self = GetService<ICurrentUser>().User.Id;

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Trip" }, TestContext.Current.CancellationToken);

        var other = await CreateNewUser();
        await groupService.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]),
            TestContext.Current.CancellationToken);

        return (group.Id, self, other.Id);
    }

    private async Task<Guid> PercentRule(Guid groupId, Guid a, decimal aPct, Guid b, decimal bPct)
    {
        var version = await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = groupId,
            Category = "Split",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal> { [a] = aPct, [b] = bPct }
            }
        }, TestContext.Current.CancellationToken);

        return version.Id;
    }

    [Fact]
    public async Task Details_for_a_transaction_that_does_not_exist_are_null()
    {
        var details = await GetService<ITransactionService>()
            .GetDetails(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(details);
    }

    /// <summary>
    /// A personal transaction has nobody to split with, so the single split is the whole
    /// amount — the default branch of the split switch.
    /// </summary>
    [Fact]
    public async Task A_personal_transaction_splits_to_the_whole_amount_for_the_payer()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 4.50m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var details = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal("Coffee", details.Name);
        Assert.Equal(4.50m, details.Amount);
        Assert.Equal(self.Id, details.PaidByUserId);

        var split = Assert.Single(details.Splits);
        Assert.Equal(4.50m, split.Amount);
    }

    [Fact]
    public async Task A_percent_transaction_splits_by_the_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var ruleVersionId = await PercentRule(groupId, self, 25m, other, 75m);
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = other,
            RuleVersionId = ruleVersionId
        }, TestContext.Current.CancellationToken);

        var details = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(100m, details.Splits.Sum(split => split.Amount));
        Assert.Contains(details.Splits, split => split.Amount == 25m);
        Assert.Contains(details.Splits, split => split.Amount == 75m);
    }

    /// <summary>
    /// The reason the payer's share is computed by subtraction rather than from their own
    /// percentage: a third of 10.00 truncates to 3.33, and three of those lose a cent. The
    /// payer absorbs it, so the splits still total the amount charged.
    /// </summary>
    [Fact]
    public async Task The_payer_absorbs_the_rounding_remainder_so_the_splits_total_the_bill()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var ruleVersionId = await PercentRule(groupId, self, 33.33m, other, 66.67m);
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Awkward",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            RuleVersionId = ruleVersionId
        }, TestContext.Current.CancellationToken);

        var details = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal(10m, details.Splits.Sum(split => split.Amount));
    }

    [Fact]
    public async Task The_edit_model_is_null_for_a_transaction_that_does_not_exist()
    {
        var model = await GetService<ITransactionService>()
            .GetUpdateModel(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(model);
    }

    [Fact]
    public async Task The_edit_model_carries_the_stored_values()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;
        var when = DateTimeOffset.UtcNow;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Taxi",
            Description = "To the airport",
            Amount = 31.20m,
            DateTime = when,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Equal("Taxi", model.Name);
        Assert.Equal("To the airport", model.Description);
        Assert.Equal(31.20m, model.Amount);
        Assert.Equal(when, model.DateTime);
        Assert.Equal(self.Id, model.PaidByUserId);
    }

    [Fact]
    public async Task An_update_changes_the_stored_transaction()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        var later = DateTimeOffset.UtcNow.AddDays(1);

        var updated = await transactions.Update(created.Id, model with
        {
            Name = "Brunch",
            Description = "Renamed",
            Amount = 12.75m,
            DateTime = later
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Brunch", updated.Name);
        Assert.Equal("Renamed", updated.Description);
        Assert.Equal(12.75m, updated.Amount);
        Assert.Equal(later, updated.DateTime);

        var reread = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal("Brunch", reread.Name);
        Assert.Equal(12.75m, reread.Amount);
    }

    [Fact]
    public async Task Updating_a_transaction_that_does_not_exist_is_rejected()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transactions.Update(Guid.NewGuid(), model, TestContext.Current.CancellationToken).AsTask());
    }

    /// <summary>
    /// The payer has to be someone the current user shares a group with. A stranger's id
    /// resolves to nothing and the update is refused rather than silently attributing the
    /// transaction to them.
    /// </summary>
    [Fact]
    public async Task An_update_naming_a_payer_outside_the_group_is_rejected()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transactions.Update(created.Id, model with { PaidByUserId = Guid.NewGuid() },
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task An_update_naming_a_rule_version_that_does_not_exist_is_rejected()
    {
        var transactions = GetService<ITransactionService>();
        var self = GetService<ICurrentUser>().User;

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self.Id
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transactions.Update(created.Id, model with { RuleVersionId = Guid.NewGuid() },
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task A_transaction_can_be_moved_onto_another_rule()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var ruleVersionId = await PercentRule(groupId, self, 50m, other, 50m);
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Shared",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self
        }, TestContext.Current.CancellationToken);

        var model = await transactions.GetUpdateModel(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(model);

        var updated = await transactions.Update(created.Id,
            model with { RuleVersionId = ruleVersionId },
            TestContext.Current.CancellationToken);

        Assert.Equal(ruleVersionId, updated.RuleVersion.Id);

        var details = await transactions.GetDetails(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(details);
        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(20m, details.Splits.Sum(split => split.Amount));
    }
}
