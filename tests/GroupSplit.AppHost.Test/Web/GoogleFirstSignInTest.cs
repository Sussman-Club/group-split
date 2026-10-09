using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

// See HomePageTest for why the analyzer is silenced: Playwright takes no CancellationToken.
#pragma warning disable xUnit1069

/// <summary>
/// Which account somebody lands in the first time they sign in with Google -- the one moment
/// Keycloak decides that from an email address rather than from an existing link.
/// </summary>
/// <remarks>
/// <para>
/// Google itself cannot take part in a test, so a second realm on the same Keycloak stands in
/// for it, and a provider pointing at that realm stands in for the Google provider. The
/// stand-in copies from the running realm's Google provider everything that decides the
/// outcome -- the first broker login flow it runs, whether it trusts the address, and the
/// claim it insists on. The realm under test may be one a data volume has kept since before
/// the file changed, so these pass only if the realm worker actually reconciled it.
/// </para>
/// <para>
/// The stand-in leaves the profile scope out on purpose. Without a family name, a realm that
/// still runs the profile review shows its form -- and the form is the attack.
/// </para>
/// </remarks>
[Collection(GoogleFirstBrokerLogin.Collection)]
public class GoogleFirstSignInTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    private readonly AppHostFixture _appHost = appHost;

    private const float OperationTimeoutMs = 60_000;

    private const string Realm = "group-split";

    private const string BrokerClientId = "group-split";

    private const string BrokerClientSecret = "stand-in-for-google";

    /// <summary>
    /// Deliberately not <see cref="KeycloakAdmin.AccountPassword"/>. With the same password, a
    /// sign-in that never left the app's realm would still succeed, and pass for a Google one.
    /// </summary>
    private const string StandInPassword = "StandIn-Google-456!";

    /// <summary>
    /// Keycloak as it reaches itself from inside its container, for the calls the broker makes
    /// server-side: exchanging the code and fetching the signing keys. The browser goes through
    /// the published port instead.
    /// </summary>
    private const string KeycloakFromInside = "http://localhost:8080";

    /// <summary>The stand-in realm, and the alias of the provider pointing at it. Unique per test.</summary>
    private readonly string _standIn = $"test-google-{Guid.NewGuid():N}"[..20];

    private enum Outcome
    {
        SignedIn,
        Refused
    }

    private KeycloakAdmin Admin => _appHost.Keycloak;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await CreateStandInForGoogleAsync();
    }

    /// <summary>
    /// Removes the stand-in, so a developer's realm -- which lives on a volume -- is not left
    /// with a provider nobody configured. Best effort: a test that failed half way through
    /// set-up may not have created everything.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var path in new[]
                 {
                     $"admin/realms/{Realm}/identity-provider/instances/{_standIn}",
                     $"admin/realms/{_standIn}"
                 })
        {
            if (await Admin.ExistsAsync(path, CancellationToken.None))
            {
                await Admin.SendAsync(HttpMethod.Delete, path, body: null, CancellationToken.None);
            }
        }

        await base.DisposeAsync();
    }

    [Fact(Timeout = 240_000)]
    public async Task A_verified_address_signs_in_to_the_account_it_belongs_to()
    {
        var account = await CreateAccountAsync();
        await CreateStandInPersonAsync(account.Email, emailVerified: true);

        Assert.Equal(Outcome.SignedIn, await SignInWithGoogleAsync(account.Email));
        Assert.Equal([_standIn], await LinksAsync(account.Id));
    }

    /// <summary>
    /// An address Google has not verified is only what somebody typed into Google. Matching
    /// an account on it would hand the account to whoever typed it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task An_address_google_has_not_verified_is_refused()
    {
        var account = await CreateAccountAsync();
        await CreateStandInPersonAsync(account.Email, emailVerified: false);

        Assert.Equal(Outcome.Refused, await SignInWithGoogleAsync(account.Email));
        Assert.Empty(await LinksAsync(account.Id));
    }

    /// <summary>
    /// The attack in #312: a Google account of one's own, and somebody else's address typed
    /// into whatever form appears between Google and the link.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task Typing_someone_elses_address_on_the_way_in_does_not_reach_their_account()
    {
        var victim = await CreateAccountAsync();
        var attacker = $"attacker-{Guid.NewGuid():N}@test.com";
        await CreateStandInPersonAsync(attacker, emailVerified: true);

        var outcome = await SignInWithGoogleAsync(attacker, typeOnTheWayIn: victim.Email);

        Assert.Empty(await LinksAsync(victim.Id));
        Assert.Equal(Outcome.Refused, outcome);
    }

    /// <summary>
    /// Starts at the app's own challenge, sends Keycloak straight to the stand-in, signs in
    /// there, and reports where it ended.
    /// </summary>
    /// <param name="typeOnTheWayIn">
    /// What to put in the email field if a profile form appears and lets it be edited.
    /// </param>
    private async Task<Outcome> SignInWithGoogleAsync(string username, string? typeOnTheWayIn = null)
    {
        // The stand-in is hidden on the login page, so the hint is what picks it -- added to
        // the authorization request the app's challenge redirects to, which names no provider.
        // Read through the context's own request client, which shares the browser's cookie
        // jar, so the correlation and nonce cookies the challenge sets are there for the
        // callback. A route cannot do this: Playwright does not route a redirect's target.
        await using (var challenge = await Context.APIRequest.GetAsync(
                   Url("auth/login?returnUrl=/groups"),
                   new APIRequestContextOptions { MaxRedirects = 0, IgnoreHTTPSErrors = true }))
        {
            var authorize = challenge.Headers["location"];

            await Page.GotoAsync($"{authorize}&kc_idp_hint={_standIn}", new PageGotoOptions { Timeout = OperationTimeoutMs });
        }

        // Only ever typed on the stand-in's own page. Anywhere else would be signing in to the
        // app's realm with a password, which proves nothing about Google.
        await Expect(Page).ToHaveURLAsync(
            new Regex($"/realms/{_standIn}/"),
            new PageAssertionsToHaveURLOptions { Timeout = OperationTimeoutMs });

        await Page.FillAsync("#username", username, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.FillAsync("#password", StandInPassword, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.ClickAsync("#kc-login", new PageClickOptions { Timeout = OperationTimeoutMs });

        var signedIn = NavLink("Groups");
        var refused = Page.Locator("#kc-error-message");
        var review = Page.Locator("#kc-idp-review-profile-form");

        await Expect(signedIn.Or(refused).Or(review).First).ToBeVisibleAsync(Visible);

        if (await review.IsVisibleAsync())
        {
            // Filled in the way the person signing in would. That includes the attacker, who
            // replaces the address if the form lets them.
            await Page.FillAsync("#firstName", "Someone", new PageFillOptions { Timeout = OperationTimeoutMs });
            await Page.FillAsync("#lastName", "Else", new PageFillOptions { Timeout = OperationTimeoutMs });

            var email = Page.Locator("#email");
            var editable = await email.IsEditableAsync();

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"The realm showed a profile review; its email field was {(editable ? "editable" : "read-only")}.");

            if (typeOnTheWayIn is not null && editable)
            {
                await email.FillAsync(typeOnTheWayIn, new LocatorFillOptions { Timeout = OperationTimeoutMs });
            }

            await review.Locator("[type=submit]").ClickAsync(new LocatorClickOptions { Timeout = OperationTimeoutMs });
            await Expect(signedIn.Or(refused).First).ToBeVisibleAsync(Visible);
        }

        return await signedIn.IsVisibleAsync() ? Outcome.SignedIn : Outcome.Refused;
    }

    /// <summary>
    /// A realm playing Google, with one confidential client the broker signs in through, and
    /// a provider on the app's realm pointing at it that copies the Google provider.
    /// </summary>
    private async Task CreateStandInForGoogleAsync()
    {
        var browserKeycloak = _appHost.Application.GetEndpoint("keycloak", "http").AbsoluteUri.TrimEnd('/');

        await Admin.SendAsync(HttpMethod.Post, "admin/realms", new
        {
            realm = _standIn,
            enabled = true,
            clients = new[]
            {
                new
                {
                    clientId = BrokerClientId,
                    enabled = true,
                    publicClient = false,
                    secret = BrokerClientSecret,
                    standardFlowEnabled = true,
                    directAccessGrantsEnabled = false,
                    redirectUris = new[] { $"{browserKeycloak}/realms/{Realm}/broker/{_standIn}/endpoint" },
                    // No profile scope: the review form only appears when a name is missing.
                    defaultClientScopes = new[] { "basic", "email" },
                    optionalClientScopes = Array.Empty<string>()
                }
            }
        }, Ct);

        var google = await Admin.GetAsync($"admin/realms/{Realm}/identity-provider/instances/google", Ct);
        var googleConfig = google.GetProperty("config");

        var config = new Dictionary<string, string>
        {
            ["clientId"] = BrokerClientId,
            ["clientSecret"] = BrokerClientSecret,
            ["clientAuthMethod"] = "client_secret_post",
            ["authorizationUrl"] = $"{browserKeycloak}/realms/{_standIn}/protocol/openid-connect/auth",
            ["tokenUrl"] = $"{KeycloakFromInside}/realms/{_standIn}/protocol/openid-connect/token",
            ["jwksUrl"] = $"{KeycloakFromInside}/realms/{_standIn}/protocol/openid-connect/certs",
            ["useJwksUrl"] = "true",
            ["validateSignature"] = "true",
            ["disableUserInfo"] = "true",
            ["defaultScope"] = "openid email",
            ["syncMode"] = Text(googleConfig, "syncMode") ?? "IMPORT"
        };

        // What decides the outcome, as the running realm has it -- absent if it is absent there.
        foreach (var name in new[] { "filteredByClaim", "claimFilterName", "claimFilterValue" })
        {
            if (Text(googleConfig, name) is { } value)
            {
                config[name] = value;
            }
        }

        await Admin.SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/identity-provider/instances", new
        {
            alias = _standIn,
            providerId = "oidc",
            enabled = true,
            hideOnLogin = true,
            trustEmail = google.GetProperty("trustEmail").GetBoolean(),
            firstBrokerLoginFlowAlias = google.GetProperty("firstBrokerLoginFlowAlias").GetString(),
            config
        }, Ct);

        static string? Text(JsonElement config, string name) =>
            config.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    /// <summary>Somebody with a Google account, as the stand-in realm knows them.</summary>
    private async Task CreateStandInPersonAsync(string email, bool emailVerified)
    {
        await Admin.SendAsync(HttpMethod.Post, $"admin/realms/{_standIn}/users", new
        {
            username = email,
            email,
            emailVerified,
            enabled = true,
            firstName = "Google",
            lastName = "Person",
            credentials = new[]
            {
                new { type = "password", value = StandInPassword, temporary = false }
            }
        }, Ct);
    }

    private async Task<(string Id, string Email)> CreateAccountAsync()
    {
        var email = await Admin.CreateAccountAsync(Ct);
        var users = await Admin.GetAsync(
            $"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", Ct);

        return (Assert.Single(users.EnumerateArray()).GetProperty("id").GetString()!, email);
    }

    /// <summary>The providers an account is linked to.</summary>
    private async Task<string[]> LinksAsync(string userId)
    {
        var links = await Admin.GetAsync($"admin/realms/{Realm}/users/{userId}/federated-identity", Ct);

        return links.EnumerateArray().Select(link => link.GetProperty("identityProvider").GetString()!).ToArray();
    }

    /// <summary>The signed-in nav item. See <c>SignedInSessionTest.NavLink</c> for the filtering.</summary>
    private ILocator NavLink(string name) =>
        Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = name, Exact = true })
            .Filter(new LocatorFilterOptions { Visible = true })
            .First;

    private static LocatorAssertionsToBeVisibleOptions Visible => new() { Timeout = OperationTimeoutMs };

    private string Url(string path) =>
        new Uri(_appHost.Application.GetEndpoint("web"), path).AbsoluteUri;
}
