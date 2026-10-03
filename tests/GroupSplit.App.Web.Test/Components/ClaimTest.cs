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
/// The page a personal invitation link lands on, and what it asks before handing a position
/// over.
/// </summary>
/// <remarks>
/// A member of the group opening somebody else's link -- to check it works before sending
/// it, most often -- used to see exactly what the invitee sees: "Join as Luz", one click
/// from merging Luz's balance into their own. Claiming is still allowed for them, because a
/// group can name the same person twice, but the page now says what it would do and asks
/// again.
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

        Assert.Contains("You are already in The flat. This invitation is made out to Luz.", page.Markup,
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
}
