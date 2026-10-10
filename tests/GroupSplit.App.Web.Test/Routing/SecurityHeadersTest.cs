using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Forwarder;

namespace GroupSplit.App.Web.Test.Routing;

/// <summary>
/// The headers every response of the app's own carries, and the ones Keycloak's responses
/// under <c>/idp</c> keep. Built the way <c>Program</c> builds the pipeline, over HTTPS on a
/// host that is not localhost, which HSTS leaves out.
/// </summary>
public sealed class SecurityHeadersTest : IAsyncLifetime
{
    private const string KeycloakPolicy = "frame-src 'self'; frame-ancestors 'self'; object-src 'none';";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseTestServer();
        builder.AddSecurityHeaders();
        builder.Services.AddHttpForwarder();
        builder.Services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(new Keycloak()));

        _app = builder.Build();
        _app.UseSecurityHeaders();
        _app.UseHsts();
        _app.MapKeycloakForwarder();
        _app.MapGet("/", (HttpContext context) =>
            Results.Content($"<script nonce=\"{SecurityHeadersExtensions.CspNonce(context)}\"></script>", "text/html"));
        _app.MapGet("/broken", () => Results.StatusCode(StatusCodes.Status500InternalServerError));

        await _app.StartAsync(Ct);

        _client = _app.GetTestClient();
        _client.BaseAddress = new Uri("https://groupsplit.test/");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The app has one-click settle, archive and delete actions behind a cookie. Framed by
    /// another site, they could be clicked by it.
    /// </summary>
    [Fact]
    public async Task No_site_may_frame_a_page()
    {
        using var response = await _client.GetAsync("/", Ct);

        Assert.Contains("frame-ancestors 'none'", Policy(response), StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task Referrers_mime_sniffing_and_transport_are_pinned()
    {
        using var response = await _client.GetAsync("/", Ct);

        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("max-age=31536000; includeSubDomains", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
    }

    /// <summary>
    /// What the pages need to load, and that a policy without it fails silently: WebAssembly
    /// cannot compile without <c>'wasm-unsafe-eval'</c>, MudBlazor positions everything with
    /// inline styles, and the fonts come from Google.
    /// </summary>
    [Theory]
    [InlineData("script-src", "'self'")]
    [InlineData("script-src", "'wasm-unsafe-eval'")]
    [InlineData("script-src", "https://cdn.plaid.com")]
    [InlineData("style-src", "'unsafe-inline'")]
    [InlineData("style-src", "https://fonts.googleapis.com")]
    [InlineData("font-src", "https://fonts.gstatic.com")]
    [InlineData("connect-src", "'self'")]
    [InlineData("connect-src", "wss://groupsplit.test")]
    [InlineData("img-src", "https://plaid-merchant-logos.plaid.com")]
    [InlineData("frame-src", "https://cdn.plaid.com")]
    [InlineData("object-src", "'none'")]
    [InlineData("base-uri", "'self'")]
    public async Task The_policy_allows_what_the_pages_load(string directive, string source)
    {
        using var response = await _client.GetAsync("/", Ct);

        Assert.Contains(source, Directive(Policy(response), directive));
    }

    /// <summary>
    /// No <c>'unsafe-inline'</c> and no <c>'unsafe-eval'</c> for scripts: the page's one
    /// inline script, the import map, is let in by a nonce that changes on every response.
    /// </summary>
    [Fact]
    public async Task Inline_scripts_need_this_responses_nonce()
    {
        using var first = await _client.GetAsync("/", Ct);
        using var second = await _client.GetAsync("/", Ct);

        var scripts = Directive(Policy(first), "script-src");

        Assert.DoesNotContain("'unsafe-inline'", scripts);
        Assert.DoesNotContain("'unsafe-eval'", scripts);

        var nonce = Assert.Single(scripts, source => source.StartsWith("'nonce-", StringComparison.Ordinal))[7..^1];

        Assert.Contains($"nonce=\"{nonce}\"", await first.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain($"'nonce-{nonce}'", Policy(second), StringComparison.Ordinal);
    }

    /// <summary>An error page is a page, and is framed as easily as any other.</summary>
    [Fact]
    public async Task An_error_response_carries_them_too()
    {
        using var response = await _client.GetAsync("/broken", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("frame-ancestors 'none'", Policy(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// Keycloak sends its own policy, one that lets its pages frame each other for its
    /// session check. Overwritten with the app's, its sign-in pages would break.
    /// </summary>
    [Fact]
    public async Task Keycloaks_own_headers_are_left_as_it_sent_them()
    {
        using var response = await _client.GetAsync("/idp/realms/group-split/protocol/openid-connect/auth", Ct);

        Assert.Equal(KeycloakPolicy, Policy(response));
        Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    private static string Policy(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    private static string[] Directive(string policy, string name) =>
        policy.Split(';', StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Single(parts => parts[0] == name)[1..];

    /// <summary>Answers the way Keycloak does, with its own security headers.</summary>
    private sealed class Keycloak : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler());

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("keycloak") };

                response.Headers.TryAddWithoutValidation("Content-Security-Policy", KeycloakPolicy);
                response.Headers.TryAddWithoutValidation("X-Frame-Options", "SAMEORIGIN");
                response.Headers.TryAddWithoutValidation("Referrer-Policy", "no-referrer");

                return Task.FromResult(response);
            }
        }
    }
}
