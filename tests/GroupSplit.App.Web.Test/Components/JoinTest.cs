using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The page a group's join link lands on: who shared it, how many are in, and one button
/// to join.
/// </summary>
/// <remarks>
/// It is reached before signing in as often as after, because a join link is how most
/// people first hear of the app. So the page asks nothing of the API until somebody is
/// signed in, sends them to sign in with the link as the place to come back to, and looks
/// the link up when they return. A link that has run out and one the group withdrew are
/// told apart on the page, which is the only thing on it.
/// </remarks>
public class JoinTest : ComponentTest
{
    private const string Token = "join-the-flat";

    private static readonly Guid Flat = Guid.NewGuid();

    private static readonly UserInfo Ana = new(Guid.NewGuid(), "Ana", "Ruiz", "ana@example.com");

    private readonly Mock<IInvitationsClient> _invitations = new();
    private readonly Mock<IGroupCommands> _commands = new();
    private readonly Mock<IAuthService> _auth = new();

    public JoinTest()
    {
        _commands
            .Setup(commands => commands.JoinByLinkAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JoinedGroupResponse(Flat, "The flat", 3, AlreadyAMember: false));

        Services.AddSingleton(_invitations.Object);
        Services.AddSingleton(_commands.Object);
        Services.AddSingleton(_auth.Object);
    }

    private void TheLinkSays(bool alreadyAMember = false, string? sharedBy = "Omar", int members = 2) =>
        _invitations
            .Setup(client => client.GetJoinLinkAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JoinLinkResponse(
                Flat, "The flat", members, sharedBy, DateTimeOffset.UtcNow.AddDays(7), alreadyAMember));

    private async Task<IRenderedComponent<Join>> Open(UserInfo? user = null)
    {
        var page = Render<Join>(parameters =>
        {
            parameters.Add(component => component.Token, Token);

            if (user is not null)
                parameters.AddCascadingValue(user);
        });

        await page.InvokeAsync(() => Task.CompletedTask);

        return page;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Join> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    // ---- signed in ------------------------------------------------------------------------

    [Fact]
    public async Task Somebody_signed_in_sees_who_shared_the_group_and_joins_it()
    {
        TheLinkSays();

        var page = await Open(Ana);

        page.WaitForAssertion(() =>
            Assert.Contains("Omar shared this group with you. 2 people are in it already.", page.Markup,
                StringComparison.Ordinal));

        await Button(page, "Join The flat").ClickAsync(new());

        _commands.Verify(commands => commands.JoinByLinkAsync(Token, It.IsAny<CancellationToken>()), Times.Once);
        Assert.EndsWith($"/groups/{Flat}", Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Following the same link twice is the ordinary thing to do with a URL in a chat
    /// thread. It is not an error: the button opens the group they are already in.
    /// </summary>
    [Fact]
    public async Task Somebody_already_in_the_group_is_offered_to_open_it()
    {
        TheLinkSays(alreadyAMember: true);

        var page = await Open(Ana);

        page.WaitForAssertion(() =>
            Assert.Contains("You are already in this group.", page.Markup, StringComparison.Ordinal));

        await Button(page, "Open The flat").ClickAsync(new());

        Assert.EndsWith($"/groups/{Flat}", Uri, StringComparison.Ordinal);
    }

    /// <summary>A link with nobody's name on it still says how many are in the group.</summary>
    [Theory]
    [InlineData(1, "One person is in it already. Join to start sharing expenses with them.")]
    [InlineData(4, "4 people are in it already. Join to start sharing expenses with them.")]
    public async Task A_link_without_a_name_on_it_says_how_many_are_in(int members, string expected)
    {
        TheLinkSays(sharedBy: null, members: members);

        var page = await Open(Ana);

        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup, StringComparison.Ordinal));
    }

    /// <summary>
    /// A join that did not go through has already said why, through the command. The page
    /// stays where it is with the button usable, so the person can try again.
    /// </summary>
    [Fact]
    public async Task A_join_that_did_not_go_through_stays_on_the_page()
    {
        TheLinkSays();

        _commands
            .Setup(commands => commands.JoinByLinkAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync((JoinedGroupResponse?)null);

        var page = await Open(Ana);
        page.WaitForAssertion(() => Button(page, "Join The flat"));

        var before = Uri;

        await Button(page, "Join The flat").ClickAsync(new());

        Assert.Equal(before, Uri);
        Assert.False(Button(page, "Join The flat").HasAttribute("disabled"));
    }

    // ---- a link that no longer works ------------------------------------------------------

    /// <summary>
    /// Run out, withdrawn, or never one of ours: three different things to have happened,
    /// and the page says which. It is the only thing on the page, so it goes there rather
    /// than in a snackbar that would scroll away and leave nothing behind it.
    /// </summary>
    [Theory]
    [InlineData(409, ErrorCodes.GroupJoinLinkExpired, "That join link has expired.")]
    [InlineData(409, ErrorCodes.GroupJoinLinkRevoked, "That join link has been withdrawn.")]
    [InlineData(404, ErrorCodes.GroupJoinLinkNotFound, "That join link is not one of ours.")]
    public async Task A_link_that_no_longer_works_says_why_on_the_page(int status, string code, string why)
    {
        _invitations
            .Setup(client => client.GetJoinLinkAsync(Token, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(status, code));

        var page = await Open(Ana);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("This link will not let you in", page.Markup, StringComparison.Ordinal);
            Assert.Contains(why, page.Markup, StringComparison.Ordinal);
        });

        Assert.DoesNotContain(page.FindAll("button"),
            button => button.TextContent.Trim().StartsWith("Join", StringComparison.Ordinal));
        Assert.Contains(page.FindAll("a"), link => link.TextContent.Trim() == "Back home");

        Snackbar.Verify(snackbar => snackbar.Add(It.IsAny<string>(), It.IsAny<Severity>(),
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);
        _commands.Verify(commands => commands.JoinByLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- not signed in --------------------------------------------------------------------

    /// <summary>
    /// Somebody not signed in is asked to sign in and brought back here, link in hand. The
    /// link is not looked up for them: the API would refuse an anonymous caller.
    /// </summary>
    [Fact]
    public async Task Somebody_not_signed_in_is_sent_to_sign_in_and_back_to_the_link()
    {
        var page = await Open();

        Assert.Contains("You have been invited to a group", page.Markup, StringComparison.Ordinal);
        _invitations.Verify(client => client.GetJoinLinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await Button(page, "Continue").ClickAsync(new());

        _auth.Verify(auth => auth.Login($"/join/{Token}", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// "Keep me signed in" means the same here as on the sign-in page: an invite link is a
    /// way into the app like any other.
    /// </summary>
    [Fact]
    public async Task Keep_me_signed_in_goes_with_the_sign_in()
    {
        var page = await Open();

        await page.Find("input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        await Button(page, "Continue").ClickAsync(new());

        _auth.Verify(auth => auth.Login($"/join/{Token}", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Signing in is the step in the middle, and it arrives as the cascading user changing
    /// rather than as a fresh page. That change is what has to send the page looking.
    /// </summary>
    [Fact]
    public void Signing_in_while_on_the_page_looks_the_link_up()
    {
        TheLinkSays();

        var host = Render<CascadingValue<UserInfo?>>(parameters => parameters
            .Add(cascade => cascade.Value, null)
            .AddChildContent<Join>(join => join.Add(component => component.Token, Token)));

        var before = host.FindComponent<Join>().Instance;
        Assert.Contains("Continue", host.Markup, StringComparison.Ordinal);

        host.Render(parameters => parameters
            .Add(cascade => cascade.Value, Ana)
            .AddChildContent<Join>(join => join.Add(component => component.Token, Token)));

        // The same page, told about the user, rather than a fresh one that started signed in.
        Assert.Same(before, host.FindComponent<Join>().Instance);

        host.WaitForAssertion(() =>
            Assert.Contains(host.FindAll("button"), button => button.TextContent.Trim() == "Join The flat"));
        _invitations.Verify(client => client.GetJoinLinkAsync(Token, It.IsAny<CancellationToken>()), Times.Once);
    }
}
