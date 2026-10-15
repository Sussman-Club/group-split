using System.Security.Claims;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.User;

/// <summary>
/// Tests for deleting one's own account. It is anonymised rather than erased: the
/// transactions are half of everyone else's history, so what goes is the name, the
/// address and the Keycloak login. See <c>docs/keycloak.md</c>, "Deleting an account".
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

        await JoinGroup(group.Id, third);

        // Settling against a debt that is not there leaves both sides out by the amount.
        await scope.ServiceProvider.GetRequiredService<ISettlementService>()
            .Settle(group.Id, new SettleRequest { UserId = third.Id, Amount = 50 }, ct);

        return (stranger, group);
    }

    private async Task<Data.Entities.Group> GroupWithAnotherMember(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var groups = GetService<IGroupService>();

        var group = await groups.CreateGroup(new CreateGroupRequest { Name = name }, ct);
        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return group;
    }

    [Fact]
    public async Task A_settled_account_loses_its_name_address_and_login()
    {
        var user = GetService<ICurrentUser>().User;

        var outstanding = await Delete();

        Assert.Empty(outstanding);

        var stored = await Stored(user.Id);

        Assert.Null(stored.FirstName);
        Assert.Null(stored.LastName);
        Assert.Null(stored.Email);
        Assert.Equal(Data.Entities.UserKind.Deleted, stored.Kind);

        // The row survives so the ledger still resolves; the Keycloak login does not.
        Assert.Equal([TestUserClaims.UserId], Keycloak.Removed);
        Assert.Empty(await Pending());
    }

    /// <summary>
    /// The subject stays on the deleted row. It names nobody once the login is gone, and it is
    /// what lets a sign-in made before Keycloak catches up be recognised as this account.
    /// </summary>
    [Fact]
    public async Task The_subject_stays_behind_as_a_tombstone()
    {
        var user = GetService<ICurrentUser>().User;

        Assert.Empty(await Delete());

        Assert.True(await HasIdentity(user.Id));
    }

    /// <summary>
    /// The bug in #360, from the API's side: the next request with the same login used to
    /// find no account and quietly provision a new, empty one, so deleting the account
    /// looked to its owner as if it had not worked.
    /// </summary>
    [Fact]
    public async Task Signing_in_again_afterwards_is_refused_rather_than_provisioned()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await Delete());

        var users = await DbContext.Set<Data.Entities.User>().CountAsync(ct);

        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() =>
            GetService<IUserProvisioner>().GetOrCreate(SameLogin(), ct));

        Assert.Equal(ErrorCodes.AccountDeleted, refusal.Code);
        Assert.Equal(users, await DbContext.Set<Data.Entities.User>().CountAsync(ct));
    }

    /// <summary>
    /// Keycloak being down does not undo or block the deletion -- it has already happened --
    /// and it does not lose the login deletion either: that is written down, with the attempt
    /// counted, for the sweep.
    /// </summary>
    [Fact]
    public async Task When_keycloak_cannot_be_reached_the_account_is_deleted_and_the_login_is_owed()
    {
        var user = GetService<ICurrentUser>().User;
        Keycloak.FailuresBeforeSuccess = int.MaxValue;

        Assert.Empty(await Delete());

        Assert.Equal(Data.Entities.UserKind.Deleted, (await Reread(user.Id)).Kind);
        Assert.Empty(Keycloak.Removed);

        var owed = Assert.Single(await Pending());

        Assert.Equal(TestUserClaims.UserId, owed.IdentityId);
        Assert.Equal(1, owed.Attempts);
    }

    /// <summary>
    /// The other half of not losing it: the login is refused at sign-in while it is owed, so
    /// an outage at Keycloak is never a window for a fresh account under the deleted login.
    /// </summary>
    [Fact]
    public async Task While_the_login_is_owed_signing_in_with_it_is_refused()
    {
        Keycloak.FailuresBeforeSuccess = int.MaxValue;

        Assert.Empty(await Delete());

        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() =>
            GetService<IUserProvisioner>().GetOrCreate(SameLogin(), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.AccountDeleted, refusal.Code);
    }

    [Fact]
    public async Task The_sweep_deletes_a_login_once_keycloak_answers_again()
    {
        Keycloak.FailuresBeforeSuccess = 1;

        Assert.Empty(await Delete());
        Assert.Empty(Keycloak.Removed);

        await PastTheGrace();
        await Sweep();

        Assert.Equal([TestUserClaims.UserId], Keycloak.Removed);
        Assert.Empty(await Pending());
    }

    /// <summary>
    /// Keycloak staying down is worth an error in the log, but never a reason to stop: giving
    /// up would leave a login standing for somebody who asked for it to go.
    /// </summary>
    [Fact]
    public async Task The_sweep_keeps_trying_however_long_keycloak_is_down()
    {
        Keycloak.FailuresBeforeSuccess = int.MaxValue;

        Assert.Empty(await Delete());
        await PastTheGrace();

        for (var sweep = 0; sweep < 5; sweep++)
        {
            await Sweep();
        }

        Assert.Equal(6, Assert.Single(await Pending()).Attempts);

        Keycloak.FailuresBeforeSuccess = 0;
        await Sweep();

        Assert.Equal([TestUserClaims.UserId], Keycloak.Removed);
        Assert.Empty(await Pending());
    }

    /// <summary>
    /// A deletion younger than a minute belongs to the request that wrote it, which tries
    /// straight after saving; the sweep leaves it alone rather than racing it.
    /// </summary>
    [Fact]
    public async Task The_sweep_leaves_a_fresh_deletion_to_the_request_that_wrote_it()
    {
        Keycloak.FailuresBeforeSuccess = int.MaxValue;

        Assert.Empty(await Delete());
        await Sweep();

        Assert.Equal(1, Keycloak.Calls);
    }

    [Fact]
    public async Task Deleting_a_deleted_account_again_changes_nothing()
    {
        var user = GetService<ICurrentUser>().User;

        Assert.Empty(await Delete());

        using var scope = ServiceProvider.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAccountService>()
            .DeleteAccount(user.Id, TestContext.Current.CancellationToken));

        Assert.Equal(1, Keycloak.Calls);
        Assert.Empty(await Pending());
    }

    private FakeKeycloakUserRemoval Keycloak => GetService<FakeKeycloakUserRemoval>();

    /// <summary>The same Keycloak subject the deleted account signed in with.</summary>
    private ClaimsPrincipal SameLogin() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, TestUserClaims.UserId)], "TestAuth"));

    private Task<List<Data.Entities.PendingLoginDeletion>> Pending() =>
        DbContext.Set<Data.Entities.PendingLoginDeletion>()
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

    /// <summary>Ages every owed deletion past the window the sweep leaves to the request.</summary>
    private async Task PastTheGrace()
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var pending in await DbContext.Set<Data.Entities.PendingLoginDeletion>().ToListAsync(ct))
        {
            pending.RequestedAt -= SweepPendingLoginDeletionsHandler.Grace + TimeSpan.FromMinutes(1);
        }

        await DbContext.SaveChangesAsync(ct);
        DbContext.ChangeTracker.Clear();
    }

    /// <summary>One run of the sweep, in a scope of its own the way the job worker runs it.</summary>
    private async Task Sweep()
    {
        using var scope = ServiceProvider.CreateScope();

        await ActivatorUtilities.CreateInstance<SweepPendingLoginDeletionsHandler>(scope.ServiceProvider)
            .HandleAsync(new SweepPendingLoginDeletions(), TestContext.Current.CancellationToken);
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
        await GetService<ISettlementService>().Settle(group.Id, new SettleRequest { UserId = other.Id, Amount = 50 }, ct);

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
        Assert.Equal(Data.Entities.UserKind.Deleted, stored.Kind);
        Assert.Equal([TestUserClaims.UserId], Keycloak.Removed);
    }

    [Fact]
    public async Task Deleting_an_account_that_is_not_there_is_refused()
    {
        var missing = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => Delete(missing));

        Assert.Contains(missing.ToString(), exception.Message);
    }
}
