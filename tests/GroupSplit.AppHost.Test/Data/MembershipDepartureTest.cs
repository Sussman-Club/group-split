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
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    /// <summary>Disarmed unless a test arms it; see <see cref="ClaimMidRead"/>.</summary>
    private ClaimMidRead? _claimMidRead;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _appHost.SeedAsync();

        var connectionString = await _appHost.Application.GetConnectionStringAsync("db", Ct);

        var services = new ServiceCollection();

        services.AddLogging();
        _claimMidRead = new ClaimMidRead(connectionString!);

        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(
            options => options.UseNpgsql(connectionString).AddInterceptors(_claimMidRead));
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

    /// <summary>
    /// The other half of the same race: somebody opening the link while somebody else claims
    /// it. Opening remembers that this person saw it, and that insert names the invitation --
    /// so if the claim deletes it in between, the insert fails its foreign key.
    /// </summary>
    /// <remarks>
    /// Made certain rather than left to the scheduler. The claim's delete is held open in a
    /// transaction of its own: the opener's reads still find the invitation, and their insert
    /// waits on the deleted row's lock. Committing the delete then is the moment the race
    /// loses, every time.
    /// </remarks>
    [Fact(Timeout = 180_000)]
    public async Task Opening_an_invitation_while_somebody_claims_it_is_a_404_not_a_500()
    {
        var people = await People(2);
        var (host, opener) = (people[0], people[1]);

        var groupId = await NewGroupOf(host, "Opened");

        string token;

        using (var scope = await As(host))
        {
            token = Assert.Single(await Service<IInvitationService>(scope).Invite(groupId,
                new InviteToGroupRequest { Names = ["Whoever opens it"] }, Ct)).Token;
        }

        using var claiming = _services!.CreateScope();
        var claimContext = Service<AppDbContext>(claiming);

        await using var claim = await claimContext.Database.BeginTransactionAsync(Ct);

        await claimContext.Set<GroupInvitation>()
            .Where(invitation => invitation.Token == token)
            .ExecuteDeleteAsync(Ct);

        using var opening = await As(opener);
        var opened = Service<IInvitationService>(opening).Describe(token, Ct);

        await UntilWaitingOnALock(opened, "InvitationOpened");
        await claim.CommitAsync(Ct);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => opened);
        Assert.Equal(ErrorCodes.GroupInvitationNotFound, refused.Code);
    }

    /// <summary>
    /// Returns once a statement touching <paramref name="table"/> is waiting on a lock, or
    /// once <paramref name="work"/> has finished without ever waiting -- which the assertions
    /// after it then report, rather than the test hanging until its timeout.
    /// </summary>
    private async Task UntilWaitingOnALock(Task work, string table)
    {
        using var scope = _services!.CreateScope();
        var context = Service<AppDbContext>(scope);

        while (!work.IsCompleted)
        {
            var waiting = await context.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND query LIKE {"%" + table + "%"}
                """).SingleAsync(Ct);

            if (waiting > 0)
                return;

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// The member's side of the same race. A member opening a link -- to check it works before
    /// sending it -- is not remembered as having opened it, so there is no insert to fail and
    /// no lock to wait on: the window is between the invitation being found and the page's
    /// answer being read. The interceptor claims the invitation in exactly that window.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task A_member_opening_an_invitation_claimed_mid_read_is_told_it_is_gone()
    {
        var people = await People(1);
        var host = people[0];

        var groupId = await NewGroupOf(host, "MemberOpens");

        string token;

        using (var scope = await As(host))
        {
            token = Assert.Single(await Service<IInvitationService>(scope).Invite(groupId,
                new InviteToGroupRequest { Names = ["Whoever claims it"] }, Ct)).Token;
        }

        _claimMidRead!.Arm(token);

        using (var scope = await As(host))
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
                Service<IInvitationService>(scope).Describe(token, Ct));

            Assert.Equal(ErrorCodes.GroupInvitationNotFound, refused.Code);
        }

        Assert.True(_claimMidRead.Fired);
    }

    /// <summary>
    /// Deletes an invitation, on a connection of its own, just before the read that answers
    /// the landing page goes out -- the moment a concurrent claim would have to land in to
    /// leave that read empty. That read is the one that joins the inviter's name, which no
    /// earlier read of the invitation does.
    /// </summary>
    private sealed class ClaimMidRead(string connectionString) : DbCommandInterceptor
    {
        private string? _token;

        public bool Fired { get; private set; }

        public void Arm(string token)
        {
            _token = token;
            Fired = false;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_token is { } token &&
                command.CommandText.Contains("FROM \"GroupInvitation\"") &&
                command.CommandText.Contains("\"FirstName\""))
            {
                _token = null;

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                await using var claim = new NpgsqlCommand(
                    "DELETE FROM \"GroupInvitation\" WHERE \"Token\" = @token", connection);
                claim.Parameters.AddWithValue("token", token);
                await claim.ExecuteNonQueryAsync(cancellationToken);

                Fired = true;
            }

            return result;
        }
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
