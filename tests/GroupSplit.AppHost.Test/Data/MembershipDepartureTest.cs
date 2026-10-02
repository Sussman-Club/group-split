using Aspire.Hosting.Testing;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// Departures, rejoining and claiming, against the real database.
/// </summary>
/// <remarks>
/// The unit suite runs on EF's in-memory provider, which has no transactions, no row locks
/// and no unique indexes. Two of the things this work depends on live exactly there: two
/// people claiming one invitation at the same moment, and the membership filter being
/// translated into every query that reaches through <c>Users</c> or <c>Groups</c>.
/// <para>
/// Each test makes its own group, so it leaves the seeded groups as it found them.
/// </para>
/// </remarks>
public class MembershipDepartureTest(AppHostFixture appHost) : IAsyncLifetime
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

    /// <summary>Three seeded people with accounts, in a stable order.</summary>
    private async Task<IReadOnlyList<Guid>> People(int count)
    {
        using var scope = _services!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<User>()
            .Where(user => user.Email != null)
            .OrderBy(user => user.Email)
            .Select(user => user.Id)
            .Take(count)
            .ToListAsync(Ct);
    }

    /// <summary>A request of its own, as one person: a scope, a context, a signed-in user.</summary>
    private async Task<IServiceScope> As(Guid userId)
    {
        var scope = _services!.CreateScope();

        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<User>()
            .FirstAsync(candidate => candidate.Id == userId, Ct);

        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().Initialize(user);

        return scope;
    }

    private static T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> NewGroupOf(Guid ownerId, string name)
    {
        using var scope = await As(ownerId);

        var group = await Service<IGroupService>(scope).CreateGroup(
            new CreateGroupRequest { Name = $"{name} {Guid.NewGuid():N}" }, Ct);

        return group.Id;
    }

    private async Task<int> MembersOf(Guid groupId, Guid userId)
    {
        using var scope = _services!.CreateScope();

        return await Service<AppDbContext>(scope).Set<Group>()
            .Where(group => group.Id == groupId)
            .SelectMany(group => group.Users)
            .CountAsync(user => user.Id == userId, Ct);
    }

    [Fact(Timeout = 180_000)]
    public async Task Two_people_claiming_one_invitation_at_once_admits_exactly_one_of_them()
    {
        var people = await People(3);
        var (host, first, second) = (people[0], people[1], people[2]);

        var groupId = await NewGroupOf(host, "Race");

        string token;
        Guid standIn;

        using (var scope = await As(host))
        {
            var invitation = Assert.Single(await Service<IInvitationService>(scope).Invite(groupId,
                new InviteToGroupRequest { Names = ["Whoever is first"] }, Ct));

            token = invitation.Token;
            standIn = invitation.ParticipantUserId;
        }

        // Released together, each on its own connection, so both are past the read of the
        // invitation before either has deleted it as often as the scheduler allows.
        var gate = new TaskCompletionSource();

        async Task<Exception?> Claim(Guid userId)
        {
            using var scope = await As(userId);
            await gate.Task;

            try
            {
                await Service<IInvitationService>(scope).Claim(token, Ct);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var claims = new[] { Claim(first), Claim(second) };
        gate.SetResult();
        var outcomes = await Task.WhenAll(claims);

        // Exactly one got in, and the other was told the link was gone -- never a 500, and
        // never a membership.
        Assert.Single(outcomes, outcome => outcome is null);

        var refused = Assert.IsType<NotFoundException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal(ErrorCodes.GroupInvitationNotFound, refused.Code);

        var admitted = await MembersOf(groupId, first) + await MembersOf(groupId, second);
        Assert.Equal(1, admitted);

        using var check = _services!.CreateScope();
        var context = Service<AppDbContext>(check);

        Assert.False(await context.Set<GroupInvitation>().AnyAsync(invitation => invitation.GroupId == groupId, Ct));
        Assert.False(await context.Set<User>().AnyAsync(user => user.Id == standIn, Ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task The_membership_filter_translates_and_hides_a_departed_row_on_postgres()
    {
        var people = await People(2);
        var (host, guest) = (people[0], people[1]);

        var groupId = await NewGroupOf(host, "Filter");

        using (var scope = await As(host))
        {
            var link = await Service<IJoinLinkService>(scope).Create(groupId, Ct);

            using (var joining = await As(guest))
                await Service<IJoinLinkService>(joining).Accept(link.Token, Ct);

            await Service<IGroupService>(scope).RemoveGroupMember(groupId, guest, Ct);
        }

        Assert.Equal(0, await MembersOf(groupId, guest));

        using (var scope = await As(host))
        {
            var members = await (await Service<IGroupService>(scope).GetGroupMembers(groupId, Ct)).ToListAsync(Ct);
            Assert.DoesNotContain(members, member => member.Id == guest);

            var loaded = await Service<AppDbContext>(scope).Set<Group>()
                .Include(group => group.Users)
                .FirstAsync(group => group.Id == groupId, Ct);
            Assert.DoesNotContain(loaded.Users, user => user.Id == guest);

            var balances = await (await Service<IGroupService>(scope).GetGroupNetBalance(groupId, Ct)).ToListAsync(Ct);
            Assert.DoesNotContain(balances, balance => balance.UserId == guest);
        }

        using (var scope = await As(guest))
        {
            Assert.Empty(await Service<IGroupService>(scope).GetGroupById(groupId, Ct));
            Assert.DoesNotContain((await Service<IGroupService>(scope).GetPosition(Ct)).Groups,
                position => position.GroupId == groupId);

            var row = await Service<AppDbContext>(scope).Set<GroupMembership>()
                .IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.GroupId == groupId && candidate.UserId == guest, Ct);

            Assert.Equal(LeftReason.Removed, row.LeftReason);
            Assert.Equal(host, row.LeftByUserId);
        }

        // And a link made after the removal brings them back, onto the same row.
        string fresh;
        using (var scope = await As(host))
            fresh = (await Service<IJoinLinkService>(scope).Create(groupId, Ct)).Token;

        using (var scope = await As(guest))
        {
            var joined = await Service<IJoinLinkService>(scope).Accept(fresh, Ct);
            Assert.False(joined.AlreadyAMember);
        }

        Assert.Equal(1, await MembersOf(groupId, guest));
    }
}
