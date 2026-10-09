using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

// See HomePageTest for why the analyzer is silenced: Playwright takes no CancellationToken.
#pragma warning disable xUnit1069

/// <summary>
/// Deleting an account deletes its Keycloak login, and the next sign-in is refused.
/// </summary>
/// <remarks>
/// <para>
/// The bug in #360, end to end: the account was anonymised here and the login was left
/// standing in Keycloak, so the next sign-in went straight through and the API quietly made
/// the person a brand-new, empty account. Deleting looked as if it had not worked.
/// </para>
/// <para>
/// This runs through every piece that has to agree for that to be fixed: the realm worker
/// creating the <c>account-deletion</c> client on a realm that may predate it, with the
/// secret the AppHost generated; the API signing in as that client and deleting the user;
/// the web app signing out locally; and Keycloak having ended the session along with the
/// user, so the sign-in that follows asks for a password rather than waving the browser
/// through -- and then refuses the password.
/// </para>
/// </remarks>
public class AccountDeletionTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    private readonly AppHostFixture _appHost = appHost;

    private const float OperationTimeoutMs = 60_000;

    private const string Realm = "group-split";

    [Fact(Timeout = 240_000)]
    public async Task A_deleted_account_cannot_sign_in_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await _appHost.Keycloak.CreateAccountAsync(ct);

        await SignInAsync(email);
        Assert.True(await LoginExistsAsync(email), "the account to delete was never created in Keycloak");

        await Page.GotoAsync(Url("account"), new PageGotoOptions { Timeout = OperationTimeoutMs });
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete my account" })
            .ClickAsync(new LocatorClickOptions { Timeout = OperationTimeoutMs });
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete account", Exact = true })
            .ClickAsync(new LocatorClickOptions { Timeout = OperationTimeoutMs });

        // Signed out of the app, locally, and told why.
        await Expect(Page).ToHaveURLAsync(
            new Regex("/login\\?reason=account-deleted$"),
            new PageAssertionsToHaveURLOptions { Timeout = OperationTimeoutMs });
        await Expect(Page.GetByText("Your account and your sign-in have been deleted"))
            .ToBeVisibleAsync(Visible);
        Assert.DoesNotContain(await Context.CookiesAsync(), cookie => cookie.Name == "GroupSplit.Auth");

        // Gone from Keycloak, not disabled: the decision recorded in docs/account-deletion.md.
        Assert.False(await LoginExistsAsync(email), "the Keycloak user survived the account's deletion");

        // The realm session went with the user. One that survived would send the browser
        // straight back into the app without asking for anything.
        await Page.GotoAsync(
            Url("auth/login?returnUrl=/groups"), new PageGotoOptions { Timeout = OperationTimeoutMs });
        await Expect(Page.Locator("#password")).ToBeVisibleAsync(Visible);

        // And the password no longer opens anything.
        await Page.FillAsync("#username", email, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.FillAsync(
            "#password", KeycloakAdmin.AccountPassword, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.ClickAsync("#kc-login", new PageClickOptions { Timeout = OperationTimeoutMs });

        await Expect(Page.GetByText("We do not recognise that email and password combination"))
            .ToBeVisibleAsync(Visible);
        Assert.DoesNotContain(await Context.CookiesAsync(), cookie => cookie.Name == "GroupSplit.Auth");
    }

    /// <summary>Whether the realm has a user with this exact address.</summary>
    private async Task<bool> LoginExistsAsync(string email)
    {
        var users = await _appHost.Keycloak.GetAsync(
            $"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true",
            TestContext.Current.CancellationToken);

        return users.GetArrayLength() > 0;
    }

    /// <summary>Signs in through Keycloak's own form and waits for the app to render signed in.</summary>
    private async Task SignInAsync(string email)
    {
        await Page.GotoAsync(
            Url("auth/login?returnUrl=/groups"), new PageGotoOptions { Timeout = OperationTimeoutMs });

        await Page.FillAsync("#username", email, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.FillAsync(
            "#password", KeycloakAdmin.AccountPassword, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.ClickAsync("#kc-login", new PageClickOptions { Timeout = OperationTimeoutMs });

        await Expect(Page.GetByText("No group yet")).ToBeVisibleAsync(Visible);
    }

    private static LocatorAssertionsToBeVisibleOptions Visible =>
        new() { Timeout = OperationTimeoutMs };

    private string Url(string path) =>
        new Uri(_appHost.Application.GetEndpoint("web"), path).AbsoluteUri;
}
