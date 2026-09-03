using System.Security.Claims;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.User;

/// <summary>
/// Tests for deleting one's own account. It is anonymised rather than erased: the
/// transactions are half of everyone else's history, so what goes is the name, the
/// address and the mapping to the Keycloak subject.
/// </summary>
public class AccountDeleteTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private Task<IReadOnlyList<OutstandingBalance>> Delete() =>
        GetService<IAccountService>().DeleteCurrentAccount(TestContext.Current.CancellationToken);

    private Task<Data.Entities.User> Stored(Guid id) =>
        DbContext.Set<Data.Entities.User>()
            .FirstAsync(candidate => candidate.Id == id, TestContext.Current.CancellationToken);

    private Task<bool> HasIdentity(Guid id) =>
        DbContext.Set<Data.Entities.UserIdentity>()
            .AnyAsync(identity => identity.User.Id == id, TestContext.Current.CancellationToken);

    private async Task<Data.Entities.Group> GroupWithAnotherMember(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var groups = GetService<IGroupService>();

        var group = await groups.CreateGroup(new CreateGroupRequest { Name = name }, ct);
        var other = await CreateNewUser();

        await groups.AddGroupMembers(
            group.Id,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]),
            ct);

        return group;
    }

    [Fact]
    public async Task A_settled_account_loses_its_name_address_and_identity()
    {
        var user = GetService<ICurrentUser>().User;

        var outstanding = await Delete();

        Assert.Empty(outstanding);

        var stored = await Stored(user.Id);

        Assert.Null(stored.FirstName);
        Assert.Null(stored.LastName);
        Assert.Null(stored.Email);

        // The row survives so the ledger still resolves; the mapping to Keycloak does not.
        Assert.False(await HasIdentity(user.Id));
    }

    [Fact]
    public async Task Deleting_leaves_the_groups_it_shared_with_others()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = GetService<ICurrentUser>().User;

        var group = await GroupWithAnotherMember("Trip");

        Assert.Empty(await Delete());

        // Read from the database rather than through IGroupService: its queries are
        // scoped to the groups the caller belongs to, and the caller has just left, so
        // asking it would say the group is gone whatever became of everyone else.
        var remaining = await DbContext.Set<Data.Entities.Group>()
            .Where(candidate => candidate.Id == group.Id)
            .SelectMany(candidate => candidate.Users)
            .Select(member => member.Id)
            .ToListAsync(ct);

        Assert.DoesNotContain(user.Id, remaining);

        // The group itself, and everyone else in it, carry on.
        Assert.NotEmpty(remaining);
    }

    [Fact]
    public async Task An_unsettled_group_blocks_deletion_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = GetService<ICurrentUser>().User;
        var email = user.Email;
        var groups = GetService<IGroupService>();

        var group = await GroupWithAnotherMember("Trip");
        var other = (await (await groups.GetGroupMembers(group.Id, ct)).ToListAsync(ct))
            .First(member => member.Id != user.Id);

        // Settling against a debt that is not there leaves both sides out by the amount,
        // which is the cheapest way to a group nobody has squared up.
        await groups.Settle(group.Id, new SettleRequest { UserId = other.Id, Amount = 50 }, ct);

        var outstanding = await Delete();

        var blocked = Assert.Single(outstanding);

        Assert.Equal(group.Id, blocked.GroupId);
        Assert.Equal("Trip", blocked.GroupName);
        Assert.NotEqual(0, blocked.Balance);

        // Refused, not partly applied: still named, still linked, still a member.
        var stored = await Stored(user.Id);

        Assert.Equal(email, stored.Email);
        Assert.True(await HasIdentity(user.Id));

        var members = await (await groups.GetGroupMembers(group.Id, ct)).ToListAsync(ct);

        Assert.Contains(members, member => member.Id == user.Id);
    }

    [Fact]
    public async Task Signing_in_again_afterwards_provisions_a_new_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = GetService<ICurrentUser>().User;

        Assert.Empty(await Delete());

        // The same Keycloak subject as before. Dropping the mapping is what stops it
        // finding the row that was just cleared and handing the account back.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, TestUserClaims.UserId)],
            "TestAuth"));

        var provisioned = await GetService<IUserProvisioner>().GetOrCreate(principal, ct);

        Assert.NotEqual(user.Id, provisioned.Id);
    }
}
