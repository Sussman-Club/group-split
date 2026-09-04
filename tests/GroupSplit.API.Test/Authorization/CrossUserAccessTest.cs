using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Authorization;

/// <summary>
/// Every read in these services is meant to be scoped to the groups the caller belongs
/// to — that scoping is the whole authorization model, since the endpoints add none of
/// their own and simply pass the id through. These tests come at each read from the
/// outside: a signed-in member of no shared group asking for someone else's data by id.
/// </summary>
public class CrossUserAccessTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// Creates a group with a percent rule and a transaction, owned entirely by a
    /// different user, and hands back the ids the current user will try to read.
    /// </summary>
    private async Task<(Guid RuleId, Guid RuleVersionId, Guid TransactionId, Guid GroupId)> SomeoneElsesGroup()
    {
        await using var scope = ServiceProvider.CreateAsyncScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        var owner = scope.ServiceProvider.GetRequiredService<ICurrentUser>().User;
        var groups = scope.ServiceProvider.GetRequiredService<IGroupService>();
        var rules = scope.ServiceProvider.GetRequiredService<IRuleService>();
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionService>();

        var group = await groups.CreateGroup(
            new CreateGroupRequest { Name = "Private" }, TestContext.Current.CancellationToken);

        var ruleVersion = await rules.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Secret category",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal> { [owner.Id] = 100 }
            }
        }, TestContext.Current.CancellationToken);

        var transaction = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Private dinner",
            Amount = 99.99m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = owner.Id,
            RuleVersionId = ruleVersion.Id
        }, TestContext.Current.CancellationToken);

        return (ruleVersion.Rule.Id, ruleVersion.Id, transaction.Id, group.Id);
    }

    /// <summary>
    /// The listing is scoped, and is the behaviour the single-item reads are measured
    /// against.
    /// </summary>
    [Fact]
    public async Task Another_members_transaction_is_not_in_my_list()
    {
        var (_, _, transactionId, _) = await SomeoneElsesGroup();

        var list = await GetService<ITransactionService>().List(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(list, transaction => transaction.Id == transactionId);
    }

    [Fact]
    public async Task Another_members_transaction_cannot_be_fetched_by_id()
    {
        var (_, _, transactionId, _) = await SomeoneElsesGroup();

        var query = await GetService<ITransactionService>()
            .Get(transactionId, TestContext.Current.CancellationToken);

        Assert.Empty(query);
    }

    /// <summary>
    /// The details response carries the amount, the group name, who paid and the full
    /// per-member split with names — everything the listing is scoped to keep private.
    /// </summary>
    [Fact]
    public async Task Another_members_transaction_details_cannot_be_read()
    {
        var (_, _, transactionId, _) = await SomeoneElsesGroup();

        var details = await GetService<ITransactionService>()
            .GetDetails(transactionId, TestContext.Current.CancellationToken);

        Assert.Null(details);
    }

    [Fact]
    public async Task Another_members_transaction_has_no_edit_model_for_me()
    {
        var (_, _, transactionId, _) = await SomeoneElsesGroup();

        var model = await GetService<ITransactionService>()
            .GetUpdateModel(transactionId, TestContext.Current.CancellationToken);

        Assert.Null(model);
    }

    [Fact]
    public async Task Another_members_rule_is_not_in_my_list()
    {
        var (ruleId, _, _, _) = await SomeoneElsesGroup();

        var list = await GetService<IRuleService>().List(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(list, version => version.Rule.Id == ruleId);
    }

    /// <summary>
    /// The rule details carry the split itself: the category, and every member's
    /// percentage or share count against their user id.
    /// </summary>
    [Fact]
    public async Task Another_members_rule_details_cannot_be_read()
    {
        var (ruleId, _, _, _) = await SomeoneElsesGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GetService<IRuleService>().GetRuleDetails(ruleId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_members_rule_cannot_be_updated()
    {
        var (ruleId, _, _, _) = await SomeoneElsesGroup();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            GetService<IRuleService>().Update(ruleId, new UpdateRuleRequest
            {
                Category = "Hijacked",
                Version = new PersonalRuleVersionDto()
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_members_rule_cannot_be_deleted()
    {
        var (ruleId, _, _, _) = await SomeoneElsesGroup();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            GetService<IRuleService>().Delete(ruleId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_members_group_cannot_be_read()
    {
        var (_, _, _, groupId) = await SomeoneElsesGroup();

        var query = await GetService<IGroupService>()
            .GetGroupById(groupId, TestContext.Current.CancellationToken);

        Assert.Empty(query);
    }

    [Fact]
    public async Task Another_members_group_membership_cannot_be_listed()
    {
        var (_, _, _, groupId) = await SomeoneElsesGroup();

        var members = await GetService<IGroupService>()
            .GetGroupMembers(groupId, TestContext.Current.CancellationToken);

        Assert.Empty(members);
    }

    /// <summary>
    /// Writing into someone else's group is the one an attacker would actually want.
    /// </summary>
    [Fact]
    public async Task I_cannot_add_myself_to_another_members_group()
    {
        var (_, _, _, groupId) = await SomeoneElsesGroup();
        var me = GetService<ICurrentUser>().User;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            GetService<IGroupService>().AddGroupMembers(groupId,
                new AddMemberRequest([new UserIdentifier { Email = me.Email! }]),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task I_cannot_add_a_rule_to_another_members_group()
    {
        var (_, _, _, groupId) = await SomeoneElsesGroup();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            GetService<IRuleService>().Create(new CreateRuleRequest
            {
                GroupId = groupId,
                Category = "Injected",
                Version = new PersonalRuleVersionDto()
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task I_cannot_record_a_transaction_against_another_members_rule()
    {
        var (_, ruleVersionId, _, _) = await SomeoneElsesGroup();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            GetService<ITransactionService>().Create(new CreateTransactionRequest
            {
                Name = "Injected",
                Amount = 1m,
                DateTime = DateTimeOffset.UtcNow,
                RuleVersionId = ruleVersionId
            }, TestContext.Current.CancellationToken).AsTask());
    }
}
