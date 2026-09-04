using System.Security.Claims;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.User;

/// <summary>
/// Tests for deleting one's own account. It is anonymised rather than erased: the
/// transactions are half of everyone else's history, so what goes is the name, the
/// address and the mapping to the Keycloak subject.
/// </summary>
public class AccountDeleteTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private Task<IReadOnlyList<OutstandingBalance>> Delete() =>
        Delete(GetService<ICurrentUser>().User.Id);

    private Task<IReadOnlyList<OutstandingBalance>> Delete(Guid userId) =>
        GetService<IAccountService>().DeleteAccount(userId, TestContext.Current.CancellationToken);

    private Task<Data.Entities.User> Stored(Guid id) =>
        DbContext.Set<Data.Entities.User>()
            .FirstAsync(candidate => candidate.Id == id, TestContext.Current.CancellationToken);

    private Task<bool> HasIdentity(Guid id) =>
        DbContext.Set<Data.Entities.UserIdentity>()
            .AnyAsync(identity => identity.User.Id == id, TestContext.Current.CancellationToken);

    /// <summary>
    /// Reads past whatever this test's own context is tracking, for the cases where the
    /// deletion ran in another scope and the stale instance would say nothing happened.
    /// </summary>
    private Task<Data.Entities.User> Reread(Guid id) =>
        DbContext.Set<Data.Entities.User>()
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == id, TestContext.Current.CancellationToken);

    /// <summary>
    /// An account the caller shares nothing with, owing money in a group the caller is
    /// not a member of.
    /// </summary>
    private async Task<(Data.Entities.User User, Data.Entities.Group Group)> StrangerOwingIn(string name)
    {
        var ct = TestContext.Current.CancellationToken;

        using var scope = ServiceProvider.CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        var stranger = scope.ServiceProvider.GetRequiredService<ICurrentUser>().User;
        var groups = scope.ServiceProvider.GetRequiredService<IGroupService>();

        var group = await groups.CreateGroup(new CreateGroupRequest { Name = name }, ct);
        var third = await CreateNewUser();

        await groups.AddGroupMembers(
            group.Id,
            new AddMemberRequest([new UserIdentifier { Email = third.Email! }]),
            ct);

        // Settling against a debt that is not there leaves both sides out by the amount.
        await groups.Settle(group.Id, new SettleRequest { UserId = third.Id, Amount = 50 }, ct);

        return (stranger, group);
    }

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

    [Fact]
    public async Task An_unsettled_group_blocks_deletion_from_outside_that_group()
    {
        var (stranger, group) = await StrangerOwingIn("Vigo");

        // The balance has to be read as the account being deleted, not as the caller.
        // Asked the caller's way, a group they are not in returns no rows at all, and no
        // rows reads exactly like a settled balance -- so the debt would be deleted over.
        var outstanding = await Delete(stranger.Id);

        var blocked = Assert.Single(outstanding);

        Assert.Equal(group.Id, blocked.GroupId);
        Assert.Equal("Vigo", blocked.GroupName);
        Assert.NotEqual(0, blocked.Balance);

        var stored = await Reread(stranger.Id);

        Assert.NotNull(stored.Email);
        Assert.True(await HasIdentity(stranger.Id));
    }

    [Fact]
    public async Task Deleting_needs_no_signed_in_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = GetService<ICurrentUser>().User;

        // What a CLI or a background job gets: a scope where nobody is signed in, so
        // ICurrentUser.User would throw. Nothing in the deletion may reach for one.
        using var scope = ServiceProvider.CreateScope();

        var outstanding = await scope.ServiceProvider.GetRequiredService<IAccountService>()
            .DeleteAccount(user.Id, ct);

        Assert.Empty(outstanding);

        var stored = await Reread(user.Id);

        Assert.Null(stored.Email);
        Assert.False(await HasIdentity(user.Id));
    }

    [Fact]
    public async Task Deleting_an_account_that_is_not_there_is_refused()
    {
        var missing = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Delete(missing));

        Assert.Contains(missing.ToString(), exception.Message);
    }
}
