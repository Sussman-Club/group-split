using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Deleting an account from the Account page, and what it does to the session it was done in.
/// </summary>
/// <remarks>
/// The sign-out afterwards is the app's own. The account's Keycloak login was deleted with it,
/// sessions included, so the ordinary sign-out -- a round trip through Keycloak's end-session
/// endpoint -- would be asking Keycloak to end a session for a user it no longer has.
/// </remarks>
public class AccountPageTest : ComponentTest
{
    private readonly Mock<IUsersClient> _users = new();
    private readonly Mock<IAuthService> _auth = new();

    public AccountPageTest()
    {
        Services.AddSingleton(_users.Object);
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(Mock.Of<IInboxStateService>());
        Services.AddSingleton(Mock.Of<IBankCommands>());
        Services.AddSingleton(Mock.Of<IBankLinkLauncher>());
    }

    [Fact]
    public async Task A_deleted_account_is_signed_out_of_the_app_only()
    {
        var (page, dialogs) = Open();

        await Delete(page, dialogs);

        _users.Verify(client => client.DeleteCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Once);
        _auth.Verify(auth => auth.SignOutDeletedAccount(It.IsAny<CancellationToken>()), Times.Once);
        _auth.Verify(auth => auth.Logout(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A refusal leaves the account, and so the session, exactly as they were.</summary>
    [Fact]
    public async Task A_refused_deletion_keeps_the_session()
    {
        _users
            .Setup(client => client.DeleteCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(Errors.Refusals.Of(409, "ACCOUNT_NOT_SETTLED"));

        var (page, dialogs) = Open();

        await Delete(page, dialogs);

        _auth.Verify(auth => auth.SignOutDeletedAccount(It.IsAny<CancellationToken>()), Times.Never);
        _auth.Verify(auth => auth.Logout(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>What the person agrees to has to include the sign-in, since that goes too.</summary>
    [Fact]
    public async Task The_confirmation_says_the_sign_in_is_deleted()
    {
        var (page, dialogs) = Open();

        _ = Button(page, "Delete my account").ClickAsync(new());

        dialogs.WaitForAssertion(() =>
            Assert.Contains("your sign-in is deleted", dialogs.Markup, StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private (IRenderedComponent<Account> Page, IRenderedComponent<MudDialogProvider> Dialogs) Open()
    {
        var provider = Render<MudDialogProvider>();

        var page = Render<Account>(parameters => parameters
            .AddCascadingValue(new UserInfo(Guid.NewGuid(), "Omar", "Haddad", "omar@example.com")));

        return (page, provider);
    }

    private static async Task Delete(IRenderedComponent<Account> page, IRenderedComponent<MudDialogProvider> dialogs)
    {
        // Held rather than awaited: the click is waiting on the confirmation it just opened.
        var deleting = Button(page, "Delete my account").ClickAsync(new());

        dialogs.WaitForAssertion(() => Assert.Contains("Delete your account?", dialogs.Markup, StringComparison.Ordinal));

        await dialogs.FindAll("button").Last(button => button.TextContent.Trim() == "Delete account").ClickAsync(new());
        await deleting;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Account> page, string text) =>
        page.FindAll("button").First(button => button.TextContent.Trim() == text);
}
