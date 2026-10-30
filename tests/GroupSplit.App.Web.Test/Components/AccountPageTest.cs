using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
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
        Services.AddSingleton<ThemePreference>();
        Services.AddSingleton(Mock.Of<IInboxStateService>());
        Services.AddSingleton(Mock.Of<IBankCommands>());
        Services.AddSingleton(Mock.Of<IBankLinkLauncher>());
    }

    [Fact]
    public void Appearance_choice_updates_the_shared_theme_preference()
    {
        var (page, _) = Open();
        page.Find("button[data-mode='Dark']").Click();
        Assert.Equal(ThemeMode.Dark, Services.GetRequiredService<ThemePreference>().Mode);
        Assert.Equal("true", page.Find("button[data-mode='Dark']").GetAttribute("aria-checked"));
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/account")]
    [InlineData("/settings?source=menu#settings-data")]
    public async Task Section_shortcuts_scroll_without_navigating_away_from_the_current_page(string route)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(route);
        var current = new Uri(navigation.Uri);
        var (page, _) = Open();
        var links = page.FindAll(".gs-settings-nav a");

        Assert.Equal(5, links.Count);
        Assert.Contains(links, link => new Uri(new Uri(navigation.BaseUri), link.GetAttribute("href")).Fragment == "#settings-help");
        Assert.Equal("/help", page.Find("#settings-help a").GetAttribute("href"));
        foreach (var link in links)
        {
            var destination = new Uri(new Uri(navigation.BaseUri), link.GetAttribute("href"));
            Assert.Equal(current.GetLeftPart(UriPartial.Query), destination.GetLeftPart(UriPartial.Query));
            Assert.Single(page.FindAll(destination.Fragment));
            Assert.True(link.HasAttribute("blazor:onclick:preventDefault"));
            await link.ClickAsync(new());
            Assert.Equal(current.AbsoluteUri, navigation.Uri);
            var call = JSInterop.Invocations["gs.scrollToSection"].Last();
            Assert.Equal(destination.Fragment[1..], Assert.Single(call.Arguments));
        }
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

    [Fact]
    public async Task A_failed_export_explains_that_the_download_can_be_retried()
    {
        JSInterop.Setup<string?>("gs.downloadPersonalData").SetResult("Your records could not be exported. Please retry.");
        var (page, _) = Open();
        await Button(page, "Download ZIP").ClickAsync(new());
        Assert.Contains("Please retry", page.Markup);
        Assert.False(Button(page, "Download ZIP").HasAttribute("disabled"));
    }

    [Fact]
    public void Native_account_uses_the_public_website_instead_of_an_unauthenticated_local_download()
    {
        Services.AddSingleton(new AppLinks(new Uri("https://groupsplit.example/")));
        var (page, _) = Open();
        Assert.Equal("https://groupsplit.example/account", page.Find("a[target='_blank']").GetAttribute("href"));
        Assert.DoesNotContain("/api/users/me/export", page.Markup);
    }

    [Fact]
    public async Task An_export_timeout_is_reported_without_breaking_the_account_page()
    {
        JSInterop.Setup<string?>("gs.downloadPersonalData").SetException(new TaskCanceledException());
        var (page, _) = Open();
        await Button(page, "Download ZIP").ClickAsync(new());
        Assert.Contains("Preparing your archive took too long", page.Markup);
        Assert.False(Button(page, "Download ZIP").HasAttribute("disabled"));
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
