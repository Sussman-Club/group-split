using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Settling;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The strip under the position: everything that needs a decision, whatever kind it is.
/// </summary>
/// <remarks>
/// These are about the invitations row, which is the one that has been away and come back.
/// An invitation used to be an email address, so the app could show somebody every group
/// waiting on them by matching their profile; a named person and a link have no address to
/// match, and for a while the row could not exist at all.
/// <para>
/// What brought it back is remembering that an account opened a link. So the row is the
/// answer to the question this design otherwise leaves hanging: somebody opens their
/// invitation, signs in, goes to look at something else -- and now has a way back to it
/// that is not the chat thread it arrived in.
/// </para>
/// </remarks>
public class WaitingOnYouTest : ComponentTest
{
    private static readonly Guid Group = Guid.NewGuid();

    private readonly Mock<ISettleStateService> _settling = new();
    private readonly Mock<IInboxStateService> _inbox = new();
    private readonly Mock<IInvitationsClient> _invitations = new();

    public WaitingOnYouTest()
    {
        // Nothing to settle and nothing in the inbox, so the invitations row is the only
        // thing the strip can be showing.
        _settling.Setup(s => s.EnsureLoadedAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _settling.SetupGet(s => s.Plan).Returns((SettlementPlanResponse?)null);

        _inbox.Setup(i => i.WantDuplicateCountAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _inbox.SetupGet(i => i.NewCount).Returns(0);

        Services.AddSingleton(_settling.Object);
        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(_invitations.Object);
    }

    private void Opened(params GroupInvitationResponse[] invitations) =>
        _invitations
            .Setup(client => client.GetMyInvitationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(invitations);

    private static GroupInvitationResponse Invitation(string name, string token) =>
        new(Guid.NewGuid(), Group, "The flat", name, token, "Anabel", DateTimeOffset.UtcNow, Guid.NewGuid());

    private IRenderedComponent<WaitingOnYou> Render() => base.Render<WaitingOnYou>();

    [Fact]
    public void An_invitation_somebody_has_opened_is_waiting_on_them()
    {
        Opened(Invitation("Carlos", "token-carlos"));

        var strip = Render();

        Assert.Contains("The flat", strip.Markup);
        Assert.Contains("Carlos", strip.Markup);
        Assert.Contains("Anabel", strip.Markup);
    }

    /// <summary>
    /// The row opens the claim page rather than claiming anything.
    /// </summary>
    /// <remarks>
    /// The old row had an Accept button, and this one must not: claiming takes on whatever
    /// the group has recorded against that name, and the page that says so is /claim. A
    /// one-tap Accept out here would be agreeing to a position nobody had been shown.
    /// </remarks>
    [Fact]
    public void The_row_leads_to_the_claim_page_and_does_not_claim()
    {
        Opened(Invitation("Carlos", "token-carlos"));

        var strip = Render();

        var link = strip.FindAll("a").Single(a => a.TextContent.Trim() == "Open");

        Assert.Equal("/claim/token-carlos", link.GetAttribute("href"));

        Assert.DoesNotContain("Accept", strip.Markup);
        Assert.DoesNotContain("Decline", strip.Markup);
    }

    [Fact]
    public void Each_open_invitation_gets_its_own_row()
    {
        Opened(Invitation("Carlos", "token-carlos"), Invitation("Nuria", "token-nuria"));

        var strip = Render();

        Assert.Equal(2, strip.FindAll("a").Count(a => a.TextContent.Trim() == "Open"));
        Assert.Contains("2", strip.Find(".gs-card-title .gs-tag").TextContent);
    }

    /// <summary>
    /// Nothing opened means nothing to decide, and the strip is not there at all -- an empty
    /// "Waiting on you" is worse than no card, because it asks somebody to read it to learn
    /// there is nothing in it.
    /// </summary>
    [Fact]
    public void Nothing_waiting_renders_nothing()
    {
        Opened();

        var strip = Render();

        Assert.DoesNotContain("Waiting on you", strip.Markup);
    }
}
