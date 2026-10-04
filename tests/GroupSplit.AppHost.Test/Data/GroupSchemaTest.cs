using Aspire.Hosting.Testing;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// What the database itself refuses about a group's people, whatever the code above it does.
/// </summary>
/// <remarks>
/// Each test writes past the services, straight at the table, the way a bug would -- and
/// expects the constraint to stop it. The unit suite cannot: its provider has no check
/// constraints, no partial indexes and no referential actions.
/// </remarks>
public class GroupSchemaTest(AppHostFixture appHost) : IAsyncLifetime
{
    private readonly AppHostFixture _appHost = appHost;

    private ServiceProvider? _services;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _appHost.SeedAsync();

        var connectionString = await _appHost.Application.GetConnectionStringAsync("db", Ct);

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(
            options => options.UseNpgsql(connectionString));
        services.AddDomainServices();
        services.AddSingleton<Amazon.S3.IAmazonS3>(Moq.Mock.Of<Amazon.S3.IAmazonS3>());
        services.Configure<ReceiptStorageOptions>(options => options.BucketName = "receipts-test");

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
    }

    private async Task<Guid> Someone()
    {
        using var scope = _services!.CreateScope();

        return await Service<AppDbContext>(scope).Set<User>()
            .Where(user => user.Email != null)
            .OrderBy(user => user.Email)
            .Select(user => user.Id)
            .FirstAsync(Ct);
    }

    private async Task<IServiceScope> As(Guid userId)
    {
        var scope = _services!.CreateScope();

        var user = await Service<AppDbContext>(scope).Set<User>()
            .FirstAsync(candidate => candidate.Id == userId, Ct);

        Service<ICurrentUserInitializer>(scope).Initialize(user);

        return scope;
    }

    private static T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    private async Task<(Guid GroupId, Guid StandIn)> AGroupWaitingOnSomebody(Guid ownerId, string name)
    {
        using var scope = await As(ownerId);

        var group = await Service<IGroupService>(scope).CreateGroup(
            new CreateGroupRequest { Name = $"{name} {Guid.NewGuid():N}" }, Ct);

        var invitation = Assert.Single(await Service<IInvitationService>(scope).Invite(group.Id,
            new InviteToGroupRequest { Names = ["Pat"] }, Ct));

        return (group.Id, invitation.ParticipantUserId);
    }

    /// <summary>Runs a statement as a bug would, and returns the constraint that refused it.</summary>
    private async Task<PostgresException> Refused(string sql, params object[] parameters)
    {
        using var scope = _services!.CreateScope();

        var refused = await Assert.ThrowsAnyAsync<PostgresException>(() =>
            Service<AppDbContext>(scope).Database.ExecuteSqlRawAsync(sql, parameters, Ct));

        return refused;
    }

    /// <summary>
    /// The one that guards money. A person's row going used to take every expense they paid
    /// and every share they held with it, and the group's balances stopped adding up with
    /// nothing to say why. Now the delete fails.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Deleting_somebody_who_paid_or_owes_is_refused_rather_than_deleting_the_money()
    {
        var owner = await Someone();
        var (groupId, standIn) = await AGroupWaitingOnSomebody(owner, "Ledger");

        using (var scope = await As(owner))
        {
            await Service<ITransactionService>(scope).Create(new CreateTransactionRequest
            {
                Name = "Pizza",
                Amount = 30m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = groupId,
                PaidByUserId = standIn,
                Splits = [new SplitInput { UserId = owner, Amount = 15m }, new SplitInput { UserId = standIn, Amount = 15m }]
            }, Ct);
        }

        // Past the invitation and the rule provisioned for them, which restrict on their own:
        // this is about the ledger's keys, so they are the only ones left to refuse.
        using (var scope = _services!.CreateScope())
        {
            await Service<AppDbContext>(scope).Database.ExecuteSqlRawAsync(
                """
                DELETE FROM "GroupInvitation" WHERE "ParticipantUserId" = {0};
                DELETE FROM "SplitRule" WHERE "ProvisionedForUserId" = {0};
                """, [standIn], Ct);
        }

        var refused = await Refused("""DELETE FROM "User" WHERE "Id" = {0}""", standIn);

        // Restrict answers at once, with its own code; the cascade it replaces answered by
        // deleting the rows.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        Assert.StartsWith("FK_Transaction", refused.ConstraintName);
    }

    /// <summary>
    /// A group whose rules cascade from it and which defaults to one of them could not be
    /// deleted at all: the restrict on its own default tripped before its row was gone.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task A_group_with_nothing_spent_in_it_can_be_deleted_with_everything_that_hangs_off_it()
    {
        var owner = await Someone();
        var (groupId, _) = await AGroupWaitingOnSomebody(owner, "Empty");

        using (var scope = _services!.CreateScope())
        {
            await Service<AppDbContext>(scope).Database.ExecuteSqlRawAsync(
                """
                DELETE FROM "GroupInvitation" WHERE "GroupId" = {0};
                DELETE FROM "Group" WHERE "Id" = {0};
                """, [groupId], Ct);
        }

        using var check = _services!.CreateScope();
        var context = Service<AppDbContext>(check);

        Assert.False(await context.Set<Group>().AnyAsync(group => group.Id == groupId, Ct));
        Assert.False(await context.Set<SplitRule>().AnyAsync(rule => rule.Group.Id == groupId, Ct));
        Assert.False(await context.Set<GroupMembershipEvent>().AnyAsync(change => change.GroupId == groupId, Ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task A_rule_a_group_defaults_to_still_cannot_be_deleted_on_its_own()
    {
        var owner = await Someone();
        var (groupId, _) = await AGroupWaitingOnSomebody(owner, "Default");

        var refused = await Refused(
            """DELETE FROM "SplitRule" WHERE "Id" = (SELECT "DefaultSplitRuleId" FROM "Group" WHERE "Id" = {0})""",
            groupId);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact(Timeout = 180_000)]
    public async Task A_half_written_departure_is_refused()
    {
        var owner = await Someone();
        var (groupId, _) = await AGroupWaitingOnSomebody(owner, "Departure");

        var reasonWithoutTime = await Refused(
            """UPDATE "GroupUser" SET "LeftReason" = 'Left' WHERE "GroupsId" = {0}""", groupId);
        Assert.Equal("CK_GroupUser_Departure", reasonWithoutTime.ConstraintName);

        var unknownReason = await Refused(
            """UPDATE "GroupUser" SET "LeftAt" = now(), "LeftReason" = 'Vanished' WHERE "GroupsId" = {0}""", groupId);
        Assert.Equal("CK_GroupUser_LeftReason", unknownReason.ConstraintName);
    }

    [Fact(Timeout = 180_000)]
    public async Task A_stand_in_cannot_be_given_an_address()
    {
        var owner = await Someone();
        var (_, standIn) = await AGroupWaitingOnSomebody(owner, "StandIn");

        var refused = await Refused(
            """UPDATE "User" SET "Email" = 'pat@example.com' WHERE "Id" = {0}""", standIn);

        Assert.Equal("CK_User_StandInHasNoEmail", refused.ConstraintName);
    }

    [Fact(Timeout = 180_000)]
    public async Task A_second_provisioned_rule_for_the_same_person_is_refused()
    {
        var owner = await Someone();
        var (groupId, standIn) = await AGroupWaitingOnSomebody(owner, "Provisioned");

        var refused = await Refused(
            """
            INSERT INTO "SplitRule" ("Id", "GroupId", "Name", "BuiltIn", "ProvisionedForUserId")
            VALUES (gen_random_uuid(), {0}, 'All for Pat again', true, {1})
            """, groupId, standIn);

        Assert.Equal("IX_SplitRule_GroupId_ProvisionedForUserId", refused.ConstraintName);
    }

    /// <summary>
    /// Two members making a join link at the same moment each revoke what they can see and
    /// insert their own. The index lets one of the inserts through; the other goes round
    /// again and revokes it. Either way the group ends with exactly one link in force.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Two_join_links_made_at_once_leave_exactly_one_in_force()
    {
        var owner = await Someone();
        var (groupId, _) = await AGroupWaitingOnSomebody(owner, "Links");

        var gate = new TaskCompletionSource();

        async Task Make()
        {
            using var scope = await As(owner);
            await gate.Task;
            await Service<IJoinLinkService>(scope).Create(groupId, Ct);
        }

        var making = new[] { Make(), Make(), Make() };
        gate.SetResult();
        await Task.WhenAll(making);

        using var check = _services!.CreateScope();

        Assert.Equal(1, await Service<AppDbContext>(check).Set<GroupJoinLink>()
            .CountAsync(link => link.GroupId == groupId && link.RevokedAt == null, Ct));

        var second = await Refused(
            """
            INSERT INTO "GroupJoinLink" ("Id", "GroupId", "Token", "CreatedAt", "ExpiresAt")
            VALUES (gen_random_uuid(), {0}, {1}, now(), now() + interval '1 day')
            """, groupId, Guid.NewGuid().ToString("N"));

        Assert.Equal("IX_GroupJoinLink_GroupId_Unrevoked", second.ConstraintName);
    }
}
