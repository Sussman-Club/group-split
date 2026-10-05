using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The page a guest's link lands on, and what it asks before handing a position over.
/// </summary>
/// <remarks>
/// A member of the group opening a guest's link -- to check it works before sending it,
/// most often -- used to see exactly what the guest sees: "Join as Luz", one click from
/// merging Luz's balance into their own. Claiming is still allowed for them, because a
/// group can add the same person twice, but the page now says what it would do and asks
/// again.
/// <para>
/// Somebody who is not the guest says so with "This isn't me", which stops the link and
/// nothing else: the page then says the group still has the guest.
/// </para>
/// </remarks>
public class ClaimTest : ComponentTest
{
    private const string Token = "token-luz";

    private static readonly Guid Flat = Guid.NewGuid();

    private readonly Mock<IInvitationsClient> _invitations = new();
    private readonly Mock<IGroupCommands> _commands = new();

    public ClaimTest()
    {
        _commands
            .Setup(commands => commands.ClaimInvitationAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClaimedResponse(
                Flat, "The flat", 3, "Luz",
                SharesTaken: 0, AmountOwed: 0m, PaymentsTaken: 0, AmountPaid: 0m, RulesTaken: 0));

        Services.AddSingleton(_invitations.Object);
        Services.AddSingleton(_commands.Object);
    }

    private void TheLinkSays(bool alreadyAMember) =>
        _invitations
            .Setup(client => client.GetInvitationClaimAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClaimResponse(
                Guid.NewGuid(), Flat, "The flat", 2, "Luz", "Ana", DateTimeOffset.UtcNow, alreadyAMember));

    /// <summary>The page, and the provider its confirmation renders into.</summary>
    private async Task<(IRenderedComponent<Claim> Page, IRenderedComponent<MudDialogProvider> Dialogs)> Open()
    {
        var provider = Render<MudDialogProvider>();

        var page = Render<Claim>(parameters => parameters
            .Add(component => component.Token, Token)
            .AddCascadingValue(new UserInfo(Guid.NewGuid(), "Ana", "Ruiz", "ana@example.com")));

        await page.InvokeAsync(() => Task.CompletedTask);

        return (page, provider);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Claim> page, string startsWith) =>
        page.FindAll("button").First(button => button.TextContent.Trim().StartsWith(startsWith, StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement DialogButton(IRenderedComponent<MudDialogProvider> dialogs, string text) =>
        dialogs.FindAll("button").Last(button => button.TextContent.Trim() == text);

    [Fact]
    public async Task A_member_claiming_an_invitation_is_asked_to_confirm_the_merge_first()
    {
        TheLinkSays(alreadyAMember: true);

        var (page, dialogs) = await Open();

        Assert.Contains("You are already in The flat. This link is for Luz, a guest.", page.Markup,
            StringComparison.Ordinal);
        Assert.Contains("merges Luz into your account", page.Markup, StringComparison.Ordinal);

        // Held rather than awaited: the click is waiting on the confirmation it just opened.
        var claiming = Button(page, "This is me: merge Luz").ClickAsync(new());

        // Asked, and nothing claimed yet.
        dialogs.WaitForAssertion(() => Assert.Contains("Merge Luz into your account", dialogs.Markup,
            StringComparison.Ordinal));
        _commands.Verify(commands => commands.ClaimInvitationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // It asks, and leaves the explaining to the page behind it rather than saying the
        // same paragraph twice in a row.
        Assert.Contains("Are you and Luz the same person?", dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("named you twice", dialogs.Markup, StringComparison.Ordinal);

        await DialogButton(dialogs, "Merge").ClickAsync(new());
        await claiming;

        _commands.Verify(commands => commands.ClaimInvitationAsync(Token, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancelling_the_merge_claims_nothing()
    {
        TheLinkSays(alreadyAMember: true);

        var (page, dialogs) = await Open();

        var claiming = Button(page, "This is me: merge Luz").ClickAsync(new());
        dialogs.WaitForAssertion(() => Assert.Contains("Merge Luz into your account", dialogs.Markup,
            StringComparison.Ordinal));

        await DialogButton(dialogs, "Cancel").ClickAsync(new());
        await claiming;

        _commands.Verify(commands => commands.ClaimInvitationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Somebody_not_in_the_group_joins_without_a_confirmation()
    {
        TheLinkSays(alreadyAMember: false);

        var (page, dialogs) = await Open();

        Assert.DoesNotContain("merges Luz into your account", page.Markup, StringComparison.Ordinal);

        await Button(page, "Join as Luz").ClickAsync(new());

        Assert.DoesNotContain("Merge Luz into your account", dialogs.Markup, StringComparison.Ordinal);
        _commands.Verify(commands => commands.ClaimInvitationAsync(Token, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The page says what the group has been doing and what taking over means, before the
    /// button that does it.
    /// </summary>
    [Fact]
    public async Task The_page_says_the_group_has_been_keeping_track_of_the_guest()
    {
        TheLinkSays(alreadyAMember: false);

        var (page, _) = await Open();

        Assert.Contains(
            "The flat has been keeping track of Luz as a guest. Join to take over: everything recorded for Luz becomes yours.",
            page.Markup, StringComparison.Ordinal);
        Assert.Contains("Ana sent you this link.", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("invited", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "This isn't me" stops the link and nothing else, and the page says so: the guest is
    /// still in the group, and the link is what stopped working.
    /// </summary>
    [Fact]
    public async Task This_isnt_me_stops_the_link_and_says_the_guest_stays()
    {
        TheLinkSays(alreadyAMember: false);

        _commands
            .Setup(commands => commands.DeclineInvitationAsync(Token, "The flat", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (page, _) = await Open();

        await Button(page, "This isn't me").ClickAsync(new());

        _commands.Verify(commands => commands.DeclineInvitationAsync(Token, "The flat", It.IsAny<CancellationToken>()),
            Times.Once);
        _commands.Verify(commands => commands.ClaimInvitationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("This link no longer works", page.Markup, StringComparison.Ordinal);
            Assert.Contains("The flat still has Luz as a guest.", page.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A refusal leaves the page where it was, so the person can try again or claim.
    /// </summary>
    [Fact]
    public async Task A_decline_that_did_not_go_through_leaves_the_link_on_the_page()
    {
        TheLinkSays(alreadyAMember: false);

        _commands
            .Setup(commands => commands.DeclineInvitationAsync(Token, "The flat", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var (page, _) = await Open();

        await Button(page, "This isn't me").ClickAsync(new());

        Assert.DoesNotContain("This link no longer works", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Join as Luz", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member checking a link is not offered the button that would stop it: for them the
    /// way off the page leaves the link working for the guest it was made for.
    /// </summary>
    [Fact]
    public async Task A_member_is_not_offered_to_stop_the_link()
    {
        TheLinkSays(alreadyAMember: true);

        var (page, _) = await Open();

        Assert.DoesNotContain(page.FindAll("button, a"),
            element => element.TextContent.Trim() == "This isn't me");
        Assert.Contains(page.FindAll("a"), element => element.TextContent.Trim() == "Back to The flat");
    }
}
