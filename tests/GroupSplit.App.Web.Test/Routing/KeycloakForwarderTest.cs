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
/// What the public origin lets through to Keycloak under <c>/idp</c>. Keycloak itself is
/// replaced by a handler that records what reached it, so each case can say both what the
/// browser was answered and whether Keycloak ever heard of the request.
/// </summary>
public sealed class KeycloakForwarderTest : IAsyncLifetime
{
    private readonly RecordingForwarder _keycloak = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddHttpForwarder();
        builder.Services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(_keycloak));

        _app = builder.Build();
        _app.MapKeycloakForwarder();

        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The paths a sign-in, the account console and the OIDC metadata the API and web app
    /// read at startup all live under. They go through with the prefix still on them, which
    /// Keycloak needs because it serves at <c>/idp</c> and scopes its cookies to it.
    /// </summary>
    [Theory]
    [InlineData("/idp/realms/group-split/.well-known/openid-configuration")]
    [InlineData("/idp/realms/group-split/protocol/openid-connect/auth?client_id=web-app")]
    [InlineData("/idp/realms/group-split/protocol/openid-connect/certs")]
    [InlineData("/idp/realms/group-split/login-actions/authenticate")]
    [InlineData("/idp/realms/group-split/broker/google/endpoint")]
    [InlineData("/idp/realms/group-split/account")]
    [InlineData("/idp/realms/group-split")]
    [InlineData("/idp/resources/abc12/login/group-split/css/login.css")]
    public async Task The_app_realm_and_its_static_files_are_forwarded(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(_keycloak.Requests);
        Assert.Equal(path, forwarded.PathAndQuery);
    }

    /// <summary>
    /// Everything that is for operators: the admin console, the admin REST API, the
    /// <c>master</c> realm and its token endpoint, any other realm, and the welcome page.
    /// None of it may reach Keycloak from the public origin.
    /// </summary>
    [Theory]
    [InlineData("/idp")]
    [InlineData("/idp/")]
    [InlineData("/idp/admin/")]
    [InlineData("/idp/admin/master/console/")]
    [InlineData("/idp/admin/realms/group-split/users")]
    [InlineData("/idp/realms/master/.well-known/openid-configuration")]
    [InlineData("/idp/realms/master/protocol/openid-connect/token")]
    [InlineData("/idp/realms/other/account")]
    [InlineData("/idp/js/keycloak.js")]
    public async Task Everything_else_under_the_prefix_is_not_found_and_never_forwarded(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_keycloak.Requests);
    }

    /// <summary>
    /// A POST to the master token endpoint is the actual attack on the admin password, so it
    /// is checked in its own right rather than trusted to follow from the GET.
    /// </summary>
    [Fact]
    public async Task The_admin_token_endpoint_does_not_accept_a_password_grant()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = "admin",
            ["password"] = "guess"
        });

        using var response = await _client.PostAsync(
            "/idp/realms/master/protocol/openid-connect/token", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_keycloak.Requests);
    }

    /// <summary>
    /// Paths that route as the app's realm here but that the next hop could read as
    /// another: an encoded slash or backslash survives routing undecoded.
    /// </summary>
    [Theory]
    [InlineData("/idp/realms/group-split/..%2F..%2Fadmin%2F")]
    [InlineData("/idp/realms/group-split/%2e%2e%2fmaster/protocol/openid-connect/token")]
    [InlineData("/idp/realms/group-split/..%5Cmaster")]
    [InlineData("/idp/resources/..%2F..%2Fadmin/")]
    public async Task A_path_with_an_encoded_separator_is_not_forwarded(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_keycloak.Requests);
    }

    [Theory]
    [InlineData("/idp/realms/group-split/account", true)]
    [InlineData("/idp/realms/group-split/a%2Fb", false)]
    [InlineData("/idp/realms/group-split/a%2fb", false)]
    [InlineData("/idp/realms/group-split/a%5cb", false)]
    [InlineData("/idp/realms/group-split/a\\b", false)]
    public void Ambiguous_paths_are_recognised(string path, bool unambiguous)
    {
        Assert.Equal(unambiguous, WebAppExtensions.IsUnambiguous(new PathString(path)));
    }

    /// <summary>Stands in for Keycloak: answers 200 to anything and remembers what it was asked.</summary>
    private sealed class RecordingForwarder : IForwarderHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(this));

        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner.Requests)
                {
                    owner.Requests.Add(request.RequestUri!);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("keycloak")
                });
            }
        }
    }
}
