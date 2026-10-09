using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// "Forgot your password?" on the running realm: offered exactly when Keycloak can send mail,
/// and, locally, where Mailpit is the relay, the mail it promises actually arrives.
/// </summary>
public partial class PasswordResetMailTest(AppHostFixture appHost)
{
    private const string Realm = "group-split";

    private static readonly TimeSpan MailWait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// What Keycloak was told about mail, and what the realm worker was told, against what the
    /// realm offers. Read rather than assumed, so the check holds for whichever value the
    /// stack was started with.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_running_realm_offers_a_password_reset_exactly_when_it_can_send_mail()
    {
        var keycloakMail = await SmtpEnabledAsync("keycloak");

        Assert.Equal(keycloakMail, await SmtpEnabledAsync("realm-settings"));

        var realm = await appHost.Keycloak.GetAsync($"admin/realms/{Realm}", Ct);

        Assert.Equal(bool.Parse(keycloakMail), realm.GetProperty("resetPasswordAllowed").GetBoolean());
    }

    /// <summary>
    /// Locally the relay is Mailpit, so the link is there, and following it ends with the mail
    /// in Mailpit's inbox rather than taken on trust.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task The_sign_in_page_offers_a_reset_whose_mail_arrives_in_mailpit()
    {
        var email = await appHost.Keycloak.CreateAccountAsync(Ct);
        var webOrigin = appHost.Application.GetEndpoint("web", "https").GetLeftPart(UriPartial.Authority);

        using var keycloak = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer()
        })
        {
            BaseAddress = appHost.Application.GetEndpoint("keycloak", "http")
        };

        using var signIn = await keycloak.GetAsync(
            $"realms/{Realm}/protocol/openid-connect/auth"
            + "?client_id=web-app&response_type=code&scope=openid"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
            + $"&redirect_uri={Uri.EscapeDataString($"{webOrigin}/signin-oidc")}",
            Ct);

        var resetLink = ResetLink().Match(await signIn.Content.ReadAsStringAsync(Ct));
        Assert.True(resetLink.Success, "The sign-in page offers no \"Forgot your password?\" link.");

        using var resetPage = await keycloak.GetAsync(WebUtility.HtmlDecode(resetLink.Groups["href"].Value), Ct);
        var form = ResetForm().Match(await resetPage.Content.ReadAsStringAsync(Ct));
        Assert.True(form.Success, "The reset page has no form to ask for the mail.");

        using var requested = await keycloak.PostAsync(
            WebUtility.HtmlDecode(form.Groups["action"].Value),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email }),
            Ct);

        Assert.True(requested.IsSuccessStatusCode || requested.StatusCode == HttpStatusCode.Found,
            $"Keycloak answered {(int)requested.StatusCode} to the reset request.");

        Assert.True(await MailArrivesAsync(email), $"No password reset mail reached Mailpit for {email}.");
    }

    private async Task<bool> MailArrivesAsync(string recipient)
    {
        using var mailpit = new HttpClient { BaseAddress = appHost.Application.GetEndpoint("mailpit", "http") };
        var deadline = DateTime.UtcNow + MailWait;

        while (DateTime.UtcNow < deadline)
        {
            using var search = JsonDocument.Parse(await mailpit.GetStringAsync(
                $"api/v1/search?query={Uri.EscapeDataString($"to:\"{recipient}\"")}", Ct));

            if (search.RootElement.GetProperty("messages_count").GetInt32() > 0) return true;

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        return false;
    }

    private async Task<string> SmtpEnabledAsync(string resourceName)
    {
        var resource = appHost.Application.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<IResourceWithEnvironment>()
            .Single(resource => resource.Name == resourceName);

        var environment = await resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);

        return Assert.Contains("SMTP_ENABLED", (IDictionary<string, string>)environment);
    }

    [GeneratedRegex(@"href=""(?<href>[^""]*login-actions/reset-credentials[^""]*)""")]
    private static partial Regex ResetLink();

    [GeneratedRegex(@"<form[^>]*\baction=""(?<action>[^""]+)""")]
    private static partial Regex ResetForm();
}
