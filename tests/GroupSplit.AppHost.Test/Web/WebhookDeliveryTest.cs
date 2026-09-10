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
    /// And it reaches the API, which refuses it. Unauthorized is the right answer to an
    /// unsigned body and is the proof the request was carried through rather than answered
    /// by the web app: nothing else on that origin refuses this way.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_unsigned_webhook_reaches_the_api_and_is_refused_there()
    {
        var ct = TestContext.Current.CancellationToken;

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = appHost.Application.GetEndpoint("web", "http")
        };

        using var response = await http.PostAsync(
            Path, new StringContent("{}", Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
