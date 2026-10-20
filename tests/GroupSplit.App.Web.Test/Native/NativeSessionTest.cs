using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services.Users;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Native;

public sealed class NativeSessionTest
{
    private static readonly NativeOptions Options = NativeOptions.Create("https://api.example/", "https://auth.example/realms/group-split", false);

    [Fact]
    public async Task Login_uses_PKCE_and_persists_only_when_remembered()
    {
        var store = new Store();
        var browser = new Browser();
        var transport = new Transport();
        var session = new NativeSession(Options, new HttpClient(transport), browser, store);
        await session.LoginAsync(true);
        var query = Fields(browser.Url!.Query.TrimStart('?'));
        var body = Fields(Assert.Single(transport.Bodies));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("native", query["client_id"]);
        Assert.Equal("groupsplit://auth/callback", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("authorization_code", body["grant_type"]);
        Assert.Equal("returned-code", body["code"]);
        Assert.True(body["code_verifier"].Length >= 43);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(body["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(challenge, query["code_challenge"]);
        Assert.NotNull(store.Value);
        Assert.Equal("access", await session.AccessTokenAsync());

        await session.LoginAsync(false);
        Assert.Null(store.Value);
        Assert.Equal("access", await session.AccessTokenAsync());
    }

    [Fact]
    public async Task Mismatched_state_never_exchanges_the_code()
    {
        var transport = new Transport();
        var session = new NativeSession(Options, new HttpClient(transport), new Browser { WrongState = true }, new Store());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoginAsync(true));
        Assert.Empty(transport.Bodies);
    }

    [Fact]
    public async Task Concurrent_requests_refresh_once_and_save_the_rotated_token()
    {
        var store = ExpiredStore();
        var transport = new Transport();
        var session = new NativeSession(Options, new HttpClient(transport), new Browser(), store);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => session.AccessTokenAsync()));
        Assert.All(results, token => Assert.Equal("access", token));
        var fields = Fields(Assert.Single(transport.Bodies));
        Assert.Equal("refresh_token", fields["grant_type"]);
        Assert.Equal("old-refresh", fields["refresh_token"]);
        Assert.Equal("refresh", JsonSerializer.Deserialize<NativeSession.Tokens>(store.Value!)!.RefreshToken);
    }

    [Fact]
    public async Task Revoked_refresh_token_clears_the_session()
    {
        var store = ExpiredStore();
        var transport = new Transport { Status = HttpStatusCode.BadRequest, Body = "{\"error\":\"invalid_grant\"}" };
        var session = new NativeSession(Options, new HttpClient(transport), new Browser(), store);
        Assert.Null(await session.AccessTokenAsync());
        Assert.Null(store.Value);
        Assert.Null(await session.AccessTokenAsync());
        Assert.Single(transport.Bodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Transient_refresh_failure_keeps_credentials_for_retry(HttpStatusCode status)
    {
        var store = ExpiredStore();
        var transport = new Transport { Status = status, Body = "{\"error\":\"temporarily_unavailable\"}" };
        var session = new NativeSession(Options, new HttpClient(transport), new Browser(), store);
        await Assert.ThrowsAsync<HttpRequestException>(() => session.AccessTokenAsync());
        Assert.NotNull(store.Value);
        transport.Status = HttpStatusCode.OK;
        transport.Body = Transport.ValidBody;
        Assert.Equal("access", await session.AccessTokenAsync());
    }

    [Fact]
    public async Task Restart_loads_the_secure_session_without_opening_a_browser()
    {
        var store = new Store { Value = JsonSerializer.Serialize(new NativeSession.Tokens("saved", "refresh", "id", DateTimeOffset.UtcNow.AddHours(1))) };
        var browser = new Browser();
        var session = new NativeSession(Options, new HttpClient(new Transport()), browser, store);
        Assert.Equal("saved", await session.AccessTokenAsync());
        Assert.Null(browser.Url);
    }

    [Fact]
    public async Task Logout_clears_locally_even_if_the_browser_is_cancelled()
    {
        var store = ExpiredStore();
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser { Cancel = true }, store);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.LogoutAsync());
        Assert.Null(store.Value);
        Assert.Null(await session.AccessTokenAsync());
    }

    [Fact]
    public async Task Deleted_account_does_not_open_the_authority()
    {
        var store = ExpiredStore();
        var browser = new Browser();
        var session = new NativeSession(Options, new HttpClient(new Transport()), browser, store);
        await session.LogoutAsync(deleted: true);
        Assert.Null(browser.Url);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Bearer_handler_attaches_token_and_clears_session_on_401_without_replaying_a_write()
    {
        var store = ExpiredStore();
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser(), store);
        var transport = new Transport { Status = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(new AuthDelegatingHandler(session) { InnerHandler = transport });
        using var response = await http.PostAsync("https://api.example/expenses", new StringContent("expense"));
        Assert.Equal("Bearer access", transport.Authorization);
        Assert.Single(transport.Bodies);
        Assert.Null(store.Value);
    }

    [Theory]
    [InlineData("http://api.example", false)]
    [InlineData("", true)]
    [InlineData("https://user:password@api.example", true)]
    [InlineData("https://api.example?key=value", true)]
    public void Invalid_build_configuration_is_rejected(string url, bool debug) =>
        Assert.Throws<InvalidOperationException>(() => NativeOptions.Create(url, "https://auth.example/realms/group-split", debug));

    [Fact]
    public void Emulator_HTTP_is_allowed_only_for_development()
    {
        var options = NativeOptions.Create("http://10.0.2.2:5000", "http://10.0.2.2:8080/realms/group-split", true);
        Assert.Equal("http://10.0.2.2:8080/realms/group-split/protocol/openid-connect/token", options.Endpoint("token").AbsoluteUri);
    }

    [Fact]
    public async Task A_delayed_401_does_not_clear_a_newer_session()
    {
        var store = ExpiredStore();
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser(), store);
        Assert.Equal("access", await session.AccessTokenAsync(TestContext.Current.CancellationToken));
        await session.InvalidateAsync("expired", TestContext.Current.CancellationToken);
        Assert.NotNull(store.Value);
        Assert.Equal("access", await session.AccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Corrupt_saved_credentials_do_not_prevent_local_sign_out()
    {
        var store = new Store { Value = "corrupt" };
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser(), store);
        await session.LogoutAsync(ct: TestContext.Current.CancellationToken);
        Assert.Null(store.Value);
        Assert.Null(await session.AccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/groups", "/groups")]
    [InlineData("//attacker.example/", "/")]
    public async Task Sign_in_refreshes_the_shared_user_and_returns_only_to_a_local_page(string destination, string expected)
    {
        using var context = new Bunit.BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var user = new Mock<IUserLogin>();
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser(), new Store());
        var auth = new AuthService(session, user.Object, navigation, Mock.Of<ISnackbar>());
        await auth.Login(destination, ct: TestContext.Current.CancellationToken);
        user.Verify(value => value.RefreshLoginAsync(), Times.Once);
        Assert.Equal(expected, new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task Cancelled_authority_logout_still_clears_the_shared_user_and_shows_sign_in()
    {
        using var context = new Bunit.BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var user = new Mock<IUserLogin>();
        var store = ExpiredStore();
        var session = new NativeSession(Options, new HttpClient(new Transport()), new Browser { Cancel = true }, store);
        var auth = new AuthService(session, user.Object, navigation, Mock.Of<ISnackbar>());
        await auth.Logout(TestContext.Current.CancellationToken);
        Assert.Null(store.Value);
        user.Verify(value => value.ClearLogin(), Times.Once);
        Assert.Equal("/login", new Uri(navigation.Uri).AbsolutePath);
    }

    private static Store ExpiredStore() => new() { Value = JsonSerializer.Serialize(new NativeSession.Tokens("expired", "old-refresh", "id", DateTimeOffset.UtcNow.AddMinutes(-1))) };
    private static Dictionary<string, string> Fields(string value) => value.Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private sealed class Store : INativeTokenStore
    {
        public string? Value { get; set; }
        public Task<string?> ReadAsync() => Task.FromResult(Value);
        public Task WriteAsync(string value) { Value = value; return Task.CompletedTask; }
        public void Clear() => Value = null;
    }
    private sealed class Browser : INativeBrowser
    {
        public Uri? Url { get; private set; }
        public bool WrongState { get; init; }
        public bool Cancel { get; init; }
        public Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callback, CancellationToken ct)
        {
            Url = url;
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            { ["state"] = WrongState ? "wrong" : Fields(url.Query.TrimStart('?'))["state"], ["code"] = "returned-code" });
        }
    }
    private sealed class Transport : HttpMessageHandler
    {
        public const string ValidBody = "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"id_token\":\"id\",\"expires_in\":300,\"token_type\":\"Bearer\"}";
        public string Body { get; set; } = ValidBody;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Bodies { get; } = [];
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            Authorization = request.Headers.Authorization?.ToString();
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
