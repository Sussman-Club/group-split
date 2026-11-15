using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Forwarder;

namespace GroupSplit.App.Web.Test.Routing;

public sealed class McpForwarderTest : IAsyncLifetime
{
    private readonly RecordingForwarder _forwarder = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpForwarder();
        builder.Services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(_forwarder));
        _app = builder.Build();
        _app.MapMcpForwarder();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _client.BaseAddress = new Uri("https://groupsplit.example");
    }
    public async ValueTask DisposeAsync() { _client.Dispose(); await _app.DisposeAsync(); }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task Forwards_public_host_bearer_path_and_challenge(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(path, _forwarder.Path);
        Assert.Equal("groupsplit.example", _forwarder.Host);
        Assert.Equal("test-token", _forwarder.Token);
    }

    private sealed class RecordingForwarder : IForwarderHttpClientFactory
    {
        public string? Path { get; private set; }
        public string? Host { get; private set; }
        public string? Token { get; private set; }
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(this));
        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Path = request.RequestUri!.PathAndQuery;
                owner.Host = request.Headers.Host;
                owner.Token = request.Headers.Authorization?.Parameter;
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer resource_metadata=\"https://groupsplit.example/.well-known/oauth-protected-resource/mcp\"");
                return Task.FromResult(response);
            }
        }
    }
}
