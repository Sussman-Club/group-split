using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GroupSplit.API.Test.Group;

public class McpHttpTests
{
    [Fact]
    public async Task Tools_exchange_the_users_token_and_do_not_bypass_api_refusals()
    {
        const string issuer = "https://identity.example/realms/group-split";
        const string resource = "https://mcp.example/mcp";
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test" };
        var transport = new FakeTransport();
        await using var factory = new WebApplicationFactory<McpHostMarker>().WithWebHostBuilder(web =>
        {
            web.UseSetting("Mcp:Issuer", issuer);
            web.UseSetting("Mcp:Resource", resource);
            web.UseSetting("Mcp:ApiBaseAddress", "https://api.example/");
            web.UseSetting("Mcp:ExchangeClientId", "exchange-client");
            web.UseSetting("Mcp:ExchangeClientSecret", "test-secret");
            web.ConfigureServices(services =>
            {
                services.AddHttpClient("exchange").ConfigurePrimaryHttpMessageHandler(() => new FakeTransport.Handler(transport, "exchange"));
                services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => new FakeTransport.Handler(transport, "api"));
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = issuer };
                    o.Configuration.SigningKeys.Add(key);
                    o.TokenValidationParameters.ValidIssuer = issuer;
                    o.TokenValidationParameters.IssuerSigningKey = key;
                });
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(resource) });
        string Token(string audience, string scope, string user = "alice") => new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(issuer, audience, [new Claim("sub", user), new Claim("scope", scope)],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        async Task<HttpResponseMessage> Call(string? token, string name, string? toolArguments = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var arguments = toolArguments ?? (name == "create_group" ? "{\"name\":\"Test\"}" : "{}");
            request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}", Encoding.UTF8, "application/json");
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
        using var anonymous = await Call(null, "list_groups");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var wrongAudience = await Call(Token("https://wrong.example/mcp", "mcp:read"), "list_groups");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
        var subjectToken = Token(resource, "mcp:read");
        using var deniedWrite = await Call(subjectToken, "create_group");
        var denied = await deniedWrite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedWrite.StatusCode == HttpStatusCode.Forbidden || denied.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, transport.ExchangeCount);
        using var list = await Call(subjectToken, "list_groups");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("\"isError\":true", await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(subjectToken, transport.SubjectToken);
        Assert.Equal("api-user-token", transport.ApiBearer);
        var otherUserToken = Token(resource, "mcp:read", "bob");
        using var otherUserList = await Call(otherUserToken, "list_groups");
        Assert.DoesNotContain("\"isError\":true", await otherUserList.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(otherUserToken, transport.SubjectToken);
        const string group = "00000000-0000-0000-0000-000000000001";
        using var activity = await Call(subjectToken, "group_activity", "{\"groupId\":\"" + group + "\",\"page\":2}");
        Assert.DoesNotContain("\"isError\":true", await activity.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("/groups/" + group + "/activity?", transport.ApiPath);
        Assert.Contains("Page=2", transport.ApiPath);
        Assert.Contains("PageSize=100", transport.ApiPath);
        using var invitation = await Call(subjectToken, "get_invitation", "{\"token\":\"https://example.test/invite/example-token\"}");
        Assert.DoesNotContain("\"isError\":true", await invitation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("example-token", transport.ApiPath);
        using var guests = await Call(subjectToken, "group_guests", "{\"groupId\":\"" + group + "\"}");
        Assert.DoesNotContain("\"isError\":true", await guests.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("/groups/" + group + "/guests", transport.ApiPath);
        using var merchants = await Call(subjectToken, "list_merchants", "{\"search\":\"coffee\"}");
        Assert.DoesNotContain("\"isError\":true", await merchants.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("coffee", transport.ApiPath);
        const string expense = "{\"expense\":{\"name\":\"Dinner\",\"amount\":12.50,\"dateTime\":\"2026-01-01T12:00:00Z\"}}";
        using var preview = await Call(subjectToken, "preview_expense", expense);
        Assert.DoesNotContain("\"isError\":true", await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, transport.ApiMethod);
        Assert.Contains("preview", transport.ApiPath);
        var exchangesBeforeWrite = transport.ExchangeCount;
        using var deniedExpense = await Call(subjectToken, "create_expense", expense);
        var deniedExpenseBody = await deniedExpense.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedExpense.StatusCode == HttpStatusCode.Forbidden || deniedExpenseBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite, transport.ExchangeCount);
        using var deniedSync = await Call(subjectToken, "sync_bank_connection", "{\"connectionId\":\"" + group + "\"}");
        var deniedSyncBody = await deniedSync.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedSync.StatusCode == HttpStatusCode.Forbidden || deniedSyncBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite, transport.ExchangeCount);
        var bankArgs = "{\"bankRowId\":\"" + group + "\",\"request\":{}}";
        using var deniedBankFile = await Call(subjectToken, "file_bank_row", bankArgs);
        var deniedBankBody = await deniedBankFile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedBankFile.StatusCode == HttpStatusCode.Forbidden || deniedBankBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite, transport.ExchangeCount);
        using var filed = await Call(Token(resource, "mcp:read mcp:write"), "file_bank_row", bankArgs);
        Assert.DoesNotContain("\"isError\":true", await filed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, transport.ApiMethod);
        Assert.Equal("/inbox/" + group + "/file", transport.ApiPath);
        Assert.DoesNotContain("\"fileAnyway\":true", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        transport.RefuseApi = true;
        using var forbidden = await Call(subjectToken, "list_groups");
        Assert.Contains("\"isError\":true", await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeTransport
    {
        public int ExchangeCount { get; private set; }
        public string? SubjectToken { get; private set; }
        public string? ApiBearer { get; private set; }
        public string ApiPath { get; private set; } = "";
        public HttpMethod? ApiMethod { get; private set; }
        public string ApiBody { get; private set; } = "";
        public bool RefuseApi { get; set; }
        public sealed class Handler(FakeTransport owner, string name) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (name == "exchange")
                {
                    owner.ExchangeCount++;
                    var form = await request.Content!.ReadAsStringAsync(ct);
                    var pairs = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                    owner.SubjectToken = pairs["subject_token"];
                    Assert.Equal("api", pairs["audience"]);
                    Assert.Equal("exchange-client", pairs["client_id"]);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"api-user-token\",\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json") };
                }
                owner.ApiBearer = request.Headers.Authorization?.Parameter;
                owner.ApiPath = request.RequestUri!.PathAndQuery;
                owner.ApiMethod = request.Method;
                owner.ApiBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                var filing = owner.ApiPath.EndsWith("/file", StringComparison.Ordinal);
                return new HttpResponseMessage(owner.RefuseApi ? HttpStatusCode.Forbidden : filing ? HttpStatusCode.Created : HttpStatusCode.OK)
                { Content = new StringContent(owner.RefuseApi ? "{}" : owner.ApiPath.Contains("/activity") ? "{\"items\":[],\"page\":2,\"pageSize\":100,\"totalCount\":0}" : owner.ApiPath.Contains("preview") ? "{\"splits\":[],\"ruleName\":null}" : owner.ApiPath.Contains("example-token") || filing ? "{}" : "[]", Encoding.UTF8, "application/json") };
            }
        }
    }
}
