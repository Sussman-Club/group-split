using System.Net;
using System.Text;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// The one hop a bank provider's webhook makes that nothing else does: arriving at the
/// public origin over plain HTTP and being carried through to the API.
/// </summary>
/// <remarks>
/// A provider POSTs once and reads the status code. It does not follow redirects, does not
/// retry a 3xx as a new request, and records anything but a 2xx as a failed delivery -- so
/// a redirect on this path is not a detour, it is the webhook never arriving.
/// <para>
/// That is what it did. In run mode the AppHost hands the web app an HTTPS endpoint, which
/// switches <c>UseHttpsRedirection</c> on, and the dev tunnel points at the plain one. Every
/// webhook was answered <c>307 Temporary Redirect</c> to <c>https://localhost:7287</c> -- an
/// address the sender could not have reached had it tried. The tunnel exists to test the
/// webhook half of bank sync locally and could not deliver a single one.
/// </para>
/// <para>
/// Deployed, nothing hands these apps an HTTPS port, so the redirection is inert and
/// production was never affected. That is exactly why it needed a test here: this is the
/// only suite where the endpoint that switches it on exists.
/// </para>
/// </remarks>
public class WebhookDeliveryTest(AppHostFixture appHost)
{
    /// <summary>
    /// Any provider segment will do. The route matches on shape, and what happens after it
    /// matches is not what this is about.
    /// </summary>
    private const string Path = "/webhooks/plaid";

    [Fact(Timeout = 120_000)]
    public async Task A_webhook_arriving_over_plain_http_is_not_redirected()
    {
        var ct = TestContext.Current.CancellationToken;

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // The plain endpoint on purpose. It is the one a tunnel or a TLS-terminating
            // proxy forwards to, and the one this used to redirect away from.
            BaseAddress = appHost.Application.GetEndpoint("web", "http")
        };

        using var response = await http.PostAsync(
            Path, new StringContent("{}", Encoding.UTF8, "application/json"), ct);

        Assert.False(
            response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                or HttpStatusCode.Found or HttpStatusCode.MovedPermanently,
            $"The webhook path answered {(int)response.StatusCode} to {response.Headers.Location}, "
            + "which a provider records as a failed delivery.");
    }

    /// <summary>
    /// And it reaches the API rather than stopping at the web app.
    /// </summary>
    /// <remarks>
    /// Asked with a provider nothing will ever register, because the alternative is a test
    /// that reads the environment rather than the code: with Plaid credentials configured
    /// this path answers 401, without them 404, and a run has no say in which. A provider
    /// the deployment does not speak is 404 either way, and only the API can produce it --
    /// the web app forwards this prefix wholesale and is excluded from the status-code
    /// pages that would otherwise turn a 404 here into its own HTML.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task A_webhook_for_a_provider_nobody_speaks_is_answered_by_the_api()
    {
        var ct = TestContext.Current.CancellationToken;

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = appHost.Application.GetEndpoint("web", "http")
        };

        using var response = await http.PostAsync(
            "/webhooks/nobody-speaks-this",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Bare, the way Results.NotFound() answers. The web app's own not-found is a
        // rendered page, so HTML here would mean the forwarder never carried the call.
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);
    }
}
