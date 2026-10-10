using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// A guest's link runs out <see cref="GroupGuest.LinkLifetime"/> after it was issued.
/// </summary>
/// <remarks>
/// Single use limits a leaked link to one taking; expiry limits how long it can be taken.
/// An unclaimed link in an old chat, a screenshot or a browser history used to hand over a
/// guest's position in the ledger whenever somebody found it. These pin down what running
/// out means: the same 404 as any link that no longer works, an honest "expired" to the
/// group, a fresh link for one press, and no half-claims at the boundary.
/// </remarks>
public class GuestLinkExpiryTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// Starts at the real time, because the parts of the app that do not read the clock --
    /// leaving a group, joining one -- stamp their rows with it, and the rejoin rule
    /// compares the two.
    /// </summary>
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private IGuestService Guests => GetService<IGuestService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(_clock);

    private Task<Data.Entities.Group> AGroup(string name = "The flat") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private async Task<GroupGuestResponse> AGuestWithALink(Guid groupId, string name = "Carlos") =>
        Assert.Single(await Guests.Add(groupId,
            new AddGuestsRequest { Names = [name], IssueLinks = true }, Ct));

    /// <summary>Somebody else, signed in within a scope of their own.</summary>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    private static T As<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>The guest row as the database holds it, ended or not.</summary>
    private Task<GroupGuest> Row(Guid standInId) =>
        DbContext.Set<GroupGuest>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(guest => guest.ParticipantUserId == standInId, Ct);

    private async Task<bool> IsMember(Guid groupId, Guid userId)
    {
        using var scope = GetService<IServiceScopeFactory>().CreateScope();

        return await As<Data.AppDbContext>(scope).Set<Data.Entities.Group>()
            .Where(candidate => candidate.Id == groupId)
            .SelectMany(candidate => candidate.Users)
            .AnyAsync(user => user.Id == userId, Ct);
    }

    private async Task<GroupGuestResponse> AsTheGroupSeesThem(Guid groupId, Guid userId) =>
        Assert.Single(await Guests.ForGroup(groupId, Ct), guest => guest.UserId == userId);

    // ---- Before and at the expiry ------------------------------------------------------

    [Fact]
    public async Task A_link_says_when_it_expires_everywhere_it_is_read()
    {
        var group = await AGroup();
        var issuedAt = _clock.GetUtcNow();
        var guest = await AGuestWithALink(group.Id);

        var expiresAt = issuedAt + GroupGuest.LinkLifetime;

        Assert.Equal(TimeSpan.FromDays(30), GroupGuest.LinkLifetime);
        Assert.True(guest.HasLink);
        Assert.False(guest.LinkExpired);
        Assert.Equal(issuedAt, guest.LinkIssuedAt);
        Assert.Equal(expiresAt, guest.LinkExpiresAt);

        Assert.Equal(expiresAt, Assert.Single(await Invitations.ForGroup(group.Id, Ct)).ExpiresAt);

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var described = await As<IInvitationService>(scope).Describe(guest.Token!, Ct);
            Assert.Equal(expiresAt, described.ExpiresAt);

            Assert.Equal(expiresAt, Assert.Single(await As<IInvitationService>(scope).Mine(Ct)).ExpiresAt);
        }
    }

    [Fact]
    public async Task A_guest_without_a_link_has_no_expiry()
    {
        var group = await AGroup();

        var guest = Assert.Single(await Guests.Add(group.Id, new AddGuestsRequest { Names = ["Carlos"] }, Ct));

        Assert.False(guest.HasLink);
        Assert.False(guest.LinkExpired);
        Assert.Null(guest.LinkExpiresAt);
    }

    [Fact]
    public async Task A_link_still_works_the_moment_before_it_expires()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        _clock.Advance(GroupGuest.LinkLifetime - TimeSpan.FromSeconds(1));

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var described = await As<IInvitationService>(scope).Describe(guest.Token!, Ct);
            Assert.Equal("Carlos", described.Name);
        }

        Assert.True((await AsTheGroupSeesThem(group.Id, guest.UserId)).HasLink);
    }

    /// <summary>
    /// The same answer as a claimed, stopped or mistyped link, in every one of the holder's
    /// routes: telling a stranger that a link expired would be telling them it was once good.
    /// </summary>
    [Fact]
    public async Task An_expired_link_answers_the_same_404_a_mistyped_one_does()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        _clock.Advance(GroupGuest.LinkLifetime);

        var (scope, stranger) = await AnotherPerson();

        using (scope)
        {
            var invitations = As<IInvitationService>(scope);

            var mistyped = await Assert.ThrowsAsync<NotFoundException>(() => invitations.Describe("not-a-link", Ct));
            var described = await Assert.ThrowsAsync<NotFoundException>(() => invitations.Describe(guest.Token!, Ct));
            var claimed = await Assert.ThrowsAsync<NotFoundException>(() => invitations.Claim(guest.Token!, Ct));
            var declined = await Assert.ThrowsAsync<NotFoundException>(() => invitations.Decline(guest.Token!, Ct));

            foreach (var refusal in new[] { described, claimed, declined })
            {
                Assert.Equal(ErrorCodes.GroupInvitationNotFound, refusal.Code);
                Assert.Equal(mistyped.Message, refusal.Message);
            }

            // Opening it did not put it in their list either.
            Assert.Empty(await invitations.Mine(Ct));
        }

        // Nothing happened: the guest is still the group's, holding their link, and nobody
        // joined.
        var row = await Row(guest.UserId);

        Assert.Null(row.EndedAt);
        Assert.Equal(guest.Token, row.Token);
        Assert.False(await IsMember(group.Id, stranger.Id));
    }

    [Fact]
    public async Task The_group_sees_a_link_that_ran_out_as_expired_and_not_as_one_to_copy()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        _clock.Advance(GroupGuest.LinkLifetime + TimeSpan.FromDays(2));

        var seen = await AsTheGroupSeesThem(group.Id, guest.UserId);

        Assert.False(seen.HasLink);
        Assert.Null(seen.Token);
        Assert.True(seen.LinkExpired);
        Assert.Equal(guest.LinkIssuedAt, seen.LinkIssuedAt);
        Assert.Equal(guest.LinkExpiresAt, seen.LinkExpiresAt);

        // The route from before guests lists links to copy, and this is not one.
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task An_expired_link_drops_out_of_the_list_of_links_somebody_opened()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var invitations = As<IInvitationService>(scope);

            await invitations.Describe(guest.Token!, Ct);
            Assert.Single(await invitations.Mine(Ct));

            _clock.Advance(GroupGuest.LinkLifetime);

            Assert.Empty(await invitations.Mine(Ct));
        }
    }

    // ---- Issuing it again --------------------------------------------------------------

    /// <summary>
    /// "Send link" on an expired link sends one that works, whether or not the member asked
    /// to replace it: the old one answers nobody, so there is nothing to keep.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issuing_a_link_again_after_it_expired_makes_a_new_one_that_works(bool replace)
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);
        var expired = guest.Token!;

        var (scope, opener) = await AnotherPerson();

        using (scope)
            await As<IInvitationService>(scope).Describe(expired, Ct);

        _clock.Advance(GroupGuest.LinkLifetime + TimeSpan.FromDays(5));
        var reissuedAt = _clock.GetUtcNow();

        var issued = await Guests.IssueLink(group.Id, guest.UserId, replace, Ct);

        Assert.True(issued.HasLink);
        Assert.False(issued.LinkExpired);
        Assert.NotEqual(expired, issued.Token);
        Assert.Equal(reissuedAt, issued.LinkIssuedAt);
        Assert.Equal(reissuedAt + GroupGuest.LinkLifetime, issued.LinkExpiresAt);

        // The ending was a proper one: whoever opened the expired link is not handed this.
        Assert.False(await DbContext.Set<InvitationOpened>().AnyAsync(row => row.UserId == opener.Id, Ct));

        // Asking again is idempotent once more.
        var again = await Guests.IssueLink(group.Id, guest.UserId, replace: false, Ct);
        Assert.Equal(issued.Token, again.Token);

        var (other, _) = await AnotherPerson();

        using (other)
        {
            var invitations = As<IInvitationService>(other);

            await Assert.ThrowsAsync<NotFoundException>(() => invitations.Describe(expired, Ct));

            var claimed = await invitations.Claim(issued.Token!, Ct);
            Assert.Equal(group.Id, claimed.GroupId);
        }
    }

    // ---- A claim at the boundary -------------------------------------------------------

    /// <summary>
    /// The claim is judged at the instant it arrives. One that arrives a second before the
    /// expiry completes, although every later read of the clock is past it -- rather than
    /// passing the first check and failing a later one with half its work done.
    /// </summary>
    [Fact]
    public async Task A_claim_that_arrives_before_the_expiry_completes_even_if_it_finishes_after()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        var (scope, carlos) = await AnotherPerson();

        using (scope)
        {
            var arrivesAt = guest.LinkExpiresAt!.Value - TimeSpan.FromSeconds(1);
            _clock.SetUtcNow(arrivesAt);

            // Every read of the clock from here on moves it a second on: the first is the
            // moment the claim arrives, and every one after it is past the expiry.
            _clock.AutoAdvanceAmount = TimeSpan.FromSeconds(1);

            var claimed = await As<IInvitationService>(scope).Claim(guest.Token!, Ct);

            _clock.AutoAdvanceAmount = TimeSpan.Zero;

            Assert.Equal(group.Id, claimed.GroupId);

            var row = await Row(guest.UserId);

            Assert.Equal(GuestEndReason.Claimed, row.EndReason);
            Assert.Equal(arrivesAt, row.EndedAt);
            Assert.Null(row.Token);
        }

        Assert.True(await IsMember(group.Id, carlos.Id));
        Assert.Empty(await Guests.ForGroup(group.Id, Ct));
    }

    /// <summary>
    /// And one that arrives at the expiry is refused before anything is written: the guest
    /// is still the group's, nobody joined, and a member can send a new link.
    /// </summary>
    [Fact]
    public async Task A_claim_that_arrives_at_the_expiry_is_refused_and_writes_nothing()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        var (scope, carlos) = await AnotherPerson();

        using (scope)
        {
            _clock.SetUtcNow(guest.LinkExpiresAt!.Value);

            var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
                As<IInvitationService>(scope).Claim(guest.Token!, Ct));

            Assert.Equal(ErrorCodes.GroupInvitationNotFound, refused.Code);
        }

        var row = await Row(guest.UserId);

        Assert.Null(row.EndedAt);
        Assert.Equal(guest.Token, row.Token);
        Assert.False(await IsMember(group.Id, carlos.Id));
        Assert.False(await DbContext.Set<GroupMembership>().IgnoreQueryFilters()
            .AnyAsync(membership => membership.GroupId == group.Id && membership.UserId == carlos.Id, Ct));

        var seen = await AsTheGroupSeesThem(group.Id, guest.UserId);
        Assert.True(seen.LinkExpired);
    }

    // ---- With the rejoin rule ----------------------------------------------------------

    /// <summary>
    /// Expiry is decided before the rejoin rule, so somebody who left after the link was
    /// issued is told what everybody else is -- not that the link predates their leaving,
    /// which would say it was once good.
    /// </summary>
    [Fact]
    public async Task An_expired_link_is_a_404_even_to_somebody_who_left_after_it_was_issued()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        var (scope, leaver) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, leaver);
            await As<IGroupService>(scope).Leave(group.Id, Ct);

            // Before it expires, the rejoin rule is what answers them.
            var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IInvitationService>(scope).Describe(guest.Token!, Ct));
            Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, conflict.Code);

            _clock.Advance(GroupGuest.LinkLifetime);

            var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
                As<IInvitationService>(scope).Describe(guest.Token!, Ct));
            Assert.Equal(ErrorCodes.GroupInvitationNotFound, refused.Code);
        }
    }

    /// <summary>
    /// Issuing again restarts both clocks the link carries: its expiry, and the date the
    /// rejoin rule reads. So a link issued again after somebody left is one issued after
    /// they left, and may bring them back.
    /// </summary>
    [Fact]
    public async Task A_link_issued_again_after_it_expired_may_bring_back_somebody_who_left_in_between()
    {
        var group = await AGroup();
        var guest = await AGuestWithALink(group.Id);

        var (scope, returning) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, returning);
            await As<IGroupService>(scope).Leave(group.Id, Ct);

            _clock.Advance(GroupGuest.LinkLifetime + TimeSpan.FromDays(1));

            var issued = await Guests.IssueLink(group.Id, guest.UserId, replace: false, Ct);

            var claimed = await As<IInvitationService>(scope).Claim(issued.Token!, Ct);

            Assert.Equal(group.Id, claimed.GroupId);
        }

        Assert.True(await IsMember(group.Id, returning.Id));
    }
}
