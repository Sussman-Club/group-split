using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// Whether the running realm keeps a record of sign-ins and admin changes, asked of Keycloak
/// itself. The realm here may be one a data volume has kept since before the file changed,
/// so these pass only if the realm worker actually reconciled it.
/// </summary>
public partial class KeycloakEventsTest(AppHostFixture appHost)
{
    private const string Realm = "group-split";

    private KeycloakAdmin Admin => appHost.Keycloak;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task The_running_realm_records_user_and_admin_events()
    {
        var config = await Admin.GetAsync($"admin/realms/{Realm}/events/config", Ct);

        Assert.True(config.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal(90 * 24 * 60 * 60, config.GetProperty("eventsExpiration").GetInt64());
        Assert.Contains("jboss-logging", Strings(config.GetProperty("eventsListeners")));
        Assert.True(config.GetProperty("adminEventsEnabled").GetBoolean());
        Assert.False(config.GetProperty("adminEventsDetailsEnabled").GetBoolean());

        var types = Strings(config.GetProperty("enabledEventTypes"));

        foreach (var type in new[]
                 {
                     "LOGIN", "LOGIN_ERROR", "IDENTITY_PROVIDER_FIRST_LOGIN", "IDENTITY_PROVIDER_LINK_ACCOUNT",
                     "FEDERATED_IDENTITY_LINK", "REMOVE_FEDERATED_IDENTITY", "UPDATE_PASSWORD", "RESET_PASSWORD",
                     "UPDATE_EMAIL", "SEND_RESET_PASSWORD", "UPDATE_PROFILE", "DELETE_ACCOUNT"
                 })
        {
            Assert.Contains(type, types);
        }
    }

    /// <summary>
    /// A wrong password and then the right one, through Keycloak's own sign-in form. Both are
    /// in the event store, and the successful one is in the container log too, which is
    /// where the jboss-logging listener writes it once its success level is raised.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task A_failed_and_a_successful_sign_in_are_recorded()
    {
        var email = await Admin.CreateAccountAsync(Ct);
        var userId = Assert.Single((await Admin.GetAsync(
                $"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", Ct))
            .EnumerateArray()).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(email, "not-the-password"));
        Assert.Equal(HttpStatusCode.Found, await SignInAsync(email, KeycloakAdmin.AccountPassword));

        var events = (await Admin.GetAsync($"admin/realms/{Realm}/events?user={userId}", Ct))
            .EnumerateArray().Select(entry => entry.GetProperty("type").GetString()).ToArray();

        Assert.Contains("LOGIN_ERROR", events);
        Assert.Contains("LOGIN", events);

        var log = await appHost.ReadLogAsync("keycloak");
        Assert.Contains(
            log.Split('\n'),
            line => line.Contains("type=\"LOGIN\"") && line.Contains(userId));
    }

    /// <summary>
    /// Creating an account is an admin event, and it is recorded without what was sent: the
    /// account was created with a password, and the event store keeps an event for ninety days.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_admin_change_is_recorded_without_the_representation_sent()
    {
        var email = await Admin.CreateAccountAsync(Ct);
        var userId = Assert.Single((await Admin.GetAsync(
                $"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", Ct))
            .EnumerateArray()).GetProperty("id").GetString()!;

        var created = (await Admin.GetAsync(
                $"admin/realms/{Realm}/admin-events?operationTypes=CREATE&resourceTypes=USER&resourcePath=users/{userId}", Ct))
            .EnumerateArray().ToArray();

        var entry = Assert.Single(created);
        Assert.False(entry.TryGetProperty("representation", out var representation)
                     && representation.ValueKind == JsonValueKind.String,
            "The admin event kept the representation that created the account, password included.");
    }

    /// <summary>
    /// Starts a sign-in for the web app, as a browser would, and submits the login form.
    /// </summary>
    /// <returns>
    /// 200 when Keycloak shows the form again, which is a refused password; 302 when it sends
    /// the browser back to the app with a code.
    /// </returns>
    private async Task<HttpStatusCode> SignInAsync(string email, string password)
    {
        var webOrigin = appHost.Application.GetEndpoint("web", "https").GetLeftPart(UriPartial.Authority);

        using var keycloak = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer()
        })
        {
            BaseAddress = appHost.Application.GetEndpoint("keycloak", "http")
        };

        using var page = await keycloak.GetAsync(
            "realms/group-split/protocol/openid-connect/auth"
            + "?client_id=web-app&response_type=code&scope=openid"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
            + $"&redirect_uri={Uri.EscapeDataString($"{webOrigin}/signin-oidc")}",
            Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var form = LoginForm().Match(await page.Content.ReadAsStringAsync(Ct)).Value;
        var action = WebUtility.HtmlDecode(FormAction().Match(form).Groups["action"].Value);

        Assert.False(string.IsNullOrEmpty(action), "The sign-in page has no login form.");

        using var submitted = await keycloak.PostAsync(action, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = email, ["password"] = password }), Ct);

        return submitted.StatusCode;
    }

    [GeneratedRegex(@"<form[^>]*\bid=""kc-form-login""[^>]*>")]
    private static partial Regex LoginForm();

    [GeneratedRegex(@"\baction=""(?<action>[^""]+)""")]
    private static partial Regex FormAction();

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();
}
