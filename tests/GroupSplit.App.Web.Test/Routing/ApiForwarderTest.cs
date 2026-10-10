using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace GroupSplit.App.Web.Test.Routing;

/// <summary>
/// The cookie-authenticated <c>/api</c> route, and what it refuses to forward. The API is
/// replaced by a handler that records what reached it, and every request is signed in, so
/// each case is about the cross-site check alone.
/// </summary>
public sealed class ApiForwarderTest : IAsyncLifetime
{
    private readonly RecordingForwarder _api = new();
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
        builder.Services.AddHttpForwarder();
        builder.Services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(_api));
        builder.Services
            .AddAuthentication(SignedIn.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SignedIn>(SignedIn.Scheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUserTokenManager, FixedToken>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapApiForwarder();

        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// What a page on another site can send without a preflight: a bodyless POST, here the
    /// shape of archiving a group, with the cookie attached and nothing else. Refused here,
    /// and the API never hears of it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("none")]
    public async Task A_write_that_does_not_show_it_came_from_the_apps_pages_is_refused(string? fetchSite)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/groups/7b0f9c3e-0000-4000-8000-000000000001/archive");

        if (fetchSite is not null)
            request.Headers.Add("Sec-Fetch-Site", fetchSite);

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);

        Assert.Equal(ErrorCodes.CrossSiteRequestRefused, problem?.Code);
        Assert.Empty(_api.Requests);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_kind_of_write_is_checked(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/transactions/7b0f9c3e-0000-4000-8000-000000000001");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_api.Requests);
    }

    /// <summary>Every current browser says so on the app's own fetches.</summary>
    [Fact]
    public async Task A_write_the_browser_marks_same_origin_is_forwarded_with_the_sessions_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/groups/7b0f9c3e-0000-4000-8000-000000000001/archive");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_api.Requests);
        Assert.Equal("/groups/7b0f9c3e-0000-4000-8000-000000000001/archive", forwarded.PathAndQuery);
        Assert.Equal($"Bearer {FixedToken.Value}", forwarded.Authorization);
    }

    /// <summary>
    /// A browser too old to send <c>Sec-Fetch-Site</c> still gets through, on the header the
    /// app's own client adds -- which another origin cannot add without a preflight.
    /// </summary>
    [Fact]
    public async Task A_write_carrying_the_apps_own_header_is_forwarded()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/groups/7b0f9c3e-0000-4000-8000-000000000001/archive");
        request.Headers.Add(SameOriginRequest.HeaderName, SameOriginRequest.HeaderValue);

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_api.Requests);
    }

    /// <summary>
    /// A read changes nothing, and a receipt opened in a new tab arrives with
    /// <c>Sec-Fetch-Site: none</c>; refusing it would only break that.
    /// </summary>
    [Fact]
    public async Task Reads_are_not_checked()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/transactions/7b0f9c3e-0000-4000-8000-000000000001/receipt-attachments/7b0f9c3e-0000-4000-8000-000000000002");
        request.Headers.Add("Sec-Fetch-Site", "none");

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_api.Requests);
    }

    [Theory]
    [InlineData("POST", null, null, false)]
    [InlineData("POST", "same-origin", null, true)]
    [InlineData("POST", "SAME-ORIGIN", null, true)]
    [InlineData("POST", "same-site", null, false)]
    [InlineData("POST", null, "1", true)]
    [InlineData("DELETE", "cross-site", null, false)]
    [InlineData("GET", "cross-site", null, true)]
    [InlineData("HEAD", null, null, true)]
    [InlineData("OPTIONS", null, null, true)]
    public void Which_requests_count_as_the_apps_own(string method, string? fetchSite, string? marker, bool own)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        if (fetchSite is not null)
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        if (marker is not null)
            context.Request.Headers[SameOriginRequest.HeaderName] = marker;

        Assert.Equal(own, WebAppExtensions.IsFromOwnPages(context.Request));
    }

    /// <summary>Signs in every request, as the cookie would for a person with a session.</summary>
    private sealed class SignedIn(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "SignedIn";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "a-subject")], Scheme)), Scheme)));
    }

    /// <summary>Hands the forwarder the same access token every time.</summary>
    private sealed class FixedToken : IUserTokenManager
    {
        public const string Value = "an-access-token";

        public Task<TokenResult<UserToken>> GetAccessTokenAsync(
            ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default) =>
            Task.FromResult<TokenResult<UserToken>>(new UserToken
            {
                AccessToken = AccessToken.Parse(Value),
                AccessTokenType = AccessTokenType.Parse("Bearer"),
                ClientId = ClientId.Parse("web-app"),
                Expiration = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        public Task RevokeRefreshTokenAsync(
            ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Stands in for the API: answers 200 to anything and remembers what it was asked.</summary>
    private sealed class RecordingForwarder : IForwarderHttpClientFactory
    {
        public List<(string PathAndQuery, string? Authorization)> Requests { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(this));

        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner.Requests)
                {
                    owner.Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
