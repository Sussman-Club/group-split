using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// A transaction has to be recorded against a rule, and the rule is what says which group
/// it belongs to. When the caller names a group but no rule — which is what the create
/// dialog sends when the group has no rules to offer — the request is refused rather than
/// quietly filed against the personal group under a name the member never chose.
/// </summary>
public class TransactionWithoutARuleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CreateTransactionRequest Request(Guid? groupId = null, Guid? ruleVersionId = null) =>
        new()
        {
            Name = "Dinner",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            RuleVersionId = ruleVersionId
        };

    [Fact]
    public async Task A_transaction_in_a_group_with_no_rules_is_refused()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Ruleless" }, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<ITransactionService>()
                .Create(Request(groupId: group.Id), TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Contains("no rule", exception.Message);
    }

    /// <summary>
    /// The failure it exists to prevent: before this check the transaction was accepted
    /// and attached to the personal group's default rule, so the member's group ledger
    /// silently stayed empty.
    /// </summary>
    [Fact]
    public async Task Such_a_transaction_is_not_filed_against_the_personal_group_instead()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Ruleless" }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<ITransactionService>()
                .Create(Request(groupId: group.Id), TestContext.Current.CancellationToken)
                .AsTask());

        Assert.False(
            await DbContext.Set<Data.Entities.Transaction>()
                .AnyAsync(TestContext.Current.CancellationToken),
            "the refused transaction must not have been saved anywhere");
    }

    /// <summary>
    /// A group that does have rules gives a different answer: the caller simply did not
    /// pick one, and the message says so rather than telling them to add a rule they
    /// already have.
    /// </summary>
    [Fact]
    public async Task A_transaction_in_a_group_that_has_rules_asks_for_one_to_be_selected()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Has rules" }, TestContext.Current.CancellationToken);

        await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Groceries",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            GetService<ITransactionService>()
                .Create(Request(groupId: group.Id), TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Contains("must be selected", exception.Message);
    }

    /// <summary>
    /// The personal group is the fallback, so naming it is not a mistake — a personal
    /// expense entered from a client that always sends the selected group still works.
    /// </summary>
    [Fact]
    public async Task Naming_the_personal_group_records_a_personal_transaction()
    {
        var personalGroupId = GetService<ICurrentUser>().User.PersonalGroup.Id;

        var created = await GetService<ITransactionService>()
            .Create(Request(groupId: personalGroupId), TestContext.Current.CancellationToken);

        Assert.Equal(20m, created.Amount);
    }

    /// <summary>
    /// The path every existing caller takes: no group, no rule, meaning a personal
    /// expense. Unchanged by the guard.
    /// </summary>
    [Fact]
    public async Task A_transaction_with_no_group_at_all_is_still_a_personal_one()
    {
        var created = await GetService<ITransactionService>()
            .Create(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("Dinner", created.Name);
    }

    /// <summary>
    /// When a rule version is given it already identifies the group, so the guard does not
    /// run and a mismatched or absent group id cannot block a valid request.
    /// </summary>
    [Fact]
    public async Task A_named_rule_version_is_enough_on_its_own()
    {
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Trip" }, TestContext.Current.CancellationToken);

        var ruleVersion = await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Food",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var created = await GetService<ITransactionService>().Create(
            Request(groupId: group.Id, ruleVersionId: ruleVersion.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(ruleVersion.Id, created.RuleVersion.Id);
    }
}
