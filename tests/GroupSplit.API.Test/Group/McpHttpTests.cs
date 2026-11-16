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
        using var currencies = await Call(subjectToken, "list_supported_currencies");
        var currenciesBody = await currencies.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"isError\":true", currenciesBody);
        Assert.Contains("USD", currenciesBody);
        Assert.Contains("EUR", currenciesBody);
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
        transport.ApiCalls.Clear();
        using var household = await Call(subjectToken, "household_summary", "{\"groupId\":\"" + group + "\",\"year\":2026,\"month\":3,\"timeZone\":\"America/New_York\"}");
        Assert.DoesNotContain("\"isError\":true", await household.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.ApiCalls);
        Assert.Contains("/groups/" + group + "/household-month", transport.ApiCalls[0]);
        Assert.Contains("America%2FNew_York", transport.ApiCalls[0]);
        Assert.Equal(HttpMethod.Get, transport.ApiMethod);
        transport.RefuseApi = true;
        using var refusedHousehold = await Call(subjectToken, "household_summary", "{\"groupId\":\"" + group + "\",\"year\":2026,\"month\":3,\"timeZone\":\"UTC\"}");
        Assert.Contains("error", await refusedHousehold.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        transport.RefuseApi = false;
        transport.ApiCalls.Clear();
        using var receiptReview = await Call(subjectToken, "review_receipt_inbox_item", "{\"receiptId\":\"" + group + "\"}");
        Assert.DoesNotContain("\"isError\":true", await receiptReview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, transport.ApiCalls.Count);
        Assert.EndsWith("/" + group, transport.ApiCalls[0]);
        Assert.EndsWith("/" + group + "/matches", transport.ApiCalls[1]);
        Assert.Equal(HttpMethod.Get, transport.ApiMethod);
        transport.RefuseApi = true;
        using var refusedReview = await Call(subjectToken, "review_receipt_inbox_item", "{\"receiptId\":\"" + group + "\"}");
        Assert.Contains("error", await refusedReview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        transport.RefuseApi = false;
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
        using var deniedDelete = await Call(subjectToken, "delete_transaction", "{\"transactionId\":\"" + group + "\"}");
        var deniedDeleteBody = await deniedDelete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedDelete.StatusCode == HttpStatusCode.Forbidden || deniedDeleteBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite, transport.ExchangeCount);
        var writeToken = Token(resource, "mcp:read mcp:write");
        using var renamed = await Call(writeToken, "rename_group", "{\"groupId\":\"" + group + "\",\"name\":\"Trip\"}");
        Assert.DoesNotContain("\"isError\":true", await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Patch, transport.ApiMethod);
        Assert.Equal("/groups/" + group, transport.ApiPath);
        Assert.Contains("Trip", transport.ApiBody);
        using var deleted = await Call(writeToken, "delete_transaction", "{\"transactionId\":\"" + group + "\"}");
        Assert.DoesNotContain("\"isError\":true", await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Delete, transport.ApiMethod);
        Assert.Equal("/transactions/" + group, transport.ApiPath);
        using var edit = await Call(writeToken, "update_transaction", "{\"transactionId\":\"" + group + "\",\"edit\":{\"name\":\"Coffee\"}}");
        Assert.DoesNotContain("\"isError\":true", await edit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Patch, transport.ApiMethod);
        Assert.Contains("Coffee", transport.ApiBody);
        Assert.DoesNotContain("splits", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        using var redivide = await Call(writeToken, "update_transaction", "{\"transactionId\":\"" + group + "\",\"edit\":{\"redivide\":true}}");
        Assert.DoesNotContain("\"isError\":true", await redivide.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("splits", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        var exchangesBeforeContradiction = transport.ExchangeCount;
        using var contradiction = await Call(writeToken, "update_transaction", "{\"transactionId\":\"" + group + "\",\"edit\":{\"groupId\":\"" + group + "\",\"makePersonal\":true}}");
        Assert.Contains("\"isError\":true", await contradiction.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(exchangesBeforeContradiction, transport.ExchangeCount);
        using var deniedSync = await Call(subjectToken, "sync_bank_connection", "{\"connectionId\":\"" + group + "\"}");
        var deniedSyncBody = await deniedSync.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedSync.StatusCode == HttpStatusCode.Forbidden || deniedSyncBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite + 4, transport.ExchangeCount);
        var bankArgs = "{\"bankRowId\":\"" + group + "\",\"request\":{}}";
        using var deniedBankFile = await Call(subjectToken, "file_bank_row", bankArgs);
        var deniedBankBody = await deniedBankFile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedBankFile.StatusCode == HttpStatusCode.Forbidden || deniedBankBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeWrite + 4, transport.ExchangeCount);
        using var filed = await Call(Token(resource, "mcp:read mcp:write"), "file_bank_row", bankArgs);
        Assert.DoesNotContain("\"isError\":true", await filed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, transport.ApiMethod);
        Assert.Equal("/inbox/" + group + "/file", transport.ApiPath);
        Assert.DoesNotContain("\"fileAnyway\":true", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        var uploadArgs = "{\"expenseId\":\"" + group + "\",\"fileName\":\"receipt.png\",\"contentBase64\":\"aGVsbG8=\"}";
        var exchangesBeforeUpload = transport.ExchangeCount;
        using var deniedUpload = await Call(subjectToken, "upload_expense_receipt", uploadArgs);
        var deniedUploadBody = await deniedUpload.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedUpload.StatusCode == HttpStatusCode.Forbidden || deniedUploadBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeUpload, transport.ExchangeCount);
        using var uploaded = await Call(writeToken, "upload_expense_receipt", uploadArgs);
        Assert.DoesNotContain("\"isError\":true", await uploaded.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, transport.ApiMethod);
        Assert.Equal("/transactions/" + group + "/receipt-attachments", transport.ApiPath);
        Assert.Contains("receipt.png", transport.ApiBody);
        Assert.Contains("hello", transport.ApiBody);
        using var downloaded = await Call(subjectToken, "download_expense_receipt", "{\"expenseId\":\"" + group + "\",\"attachmentId\":\"" + group + "\"}");
        var downloadedBody = await downloaded.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"isError\":true", downloadedBody);
        Assert.Contains("aGVsbG8=", downloadedBody);
        Assert.Equal(HttpMethod.Get, transport.ApiMethod);
        Assert.Equal("/transactions/" + group + "/receipt-attachments/" + group, transport.ApiPath);
        var exchangesBeforeInvalidUpload = transport.ExchangeCount;
        using var invalidUpload = await Call(writeToken, "upload_expense_receipt", "{\"expenseId\":\"" + group + "\",\"fileName\":\"receipt.exe\",\"contentBase64\":\"aGVsbG8=\"}");
        Assert.Contains("\"isError\":true", await invalidUpload.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(exchangesBeforeInvalidUpload, transport.ExchangeCount);
        var receiptEditArgs = "{\"expenseId\":\"" + group + "\",\"itemId\":\"" + group + "\",\"edit\":{\"name\":\"Coffee\"}}";
        using var deniedReceiptEdit = await Call(subjectToken, "update_receipt_item", receiptEditArgs);
        var deniedReceiptEditBody = await deniedReceiptEdit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedReceiptEdit.StatusCode == HttpStatusCode.Forbidden || deniedReceiptEditBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(exchangesBeforeInvalidUpload, transport.ExchangeCount);
        using var receiptEdit = await Call(writeToken, "update_receipt_item", receiptEditArgs);
        Assert.DoesNotContain("\"isError\":true", await receiptEdit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Patch, transport.ApiMethod);
        Assert.Contains("/items/" + group, transport.ApiPath);
        Assert.Contains("Coffee", transport.ApiBody);
        Assert.DoesNotContain("taxRate", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        var reportArgs = "{\"report\":{\"title\":\"Help\",\"description\":\"Issue\"}}";
        var beforeReport = transport.ExchangeCount;
        using var deniedReport = await Call(subjectToken, "create_support_report", reportArgs);
        var deniedReportBody = await deniedReport.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(deniedReport.StatusCode == HttpStatusCode.Forbidden || deniedReportBody.Contains("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeReport, transport.ExchangeCount);
        using var createdReport = await Call(writeToken, "create_support_report", reportArgs);
        Assert.DoesNotContain("\"isError\":true", await createdReport.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, transport.ApiMethod);
        Assert.StartsWith("/support-reports", transport.ApiPath);
        Assert.Contains("Help", transport.ApiBody);
        var beforeExcludedTool = transport.ExchangeCount;
        using var excludedAccountDelete = await Call(writeToken, "delete_current_account");
        Assert.Contains("error", await excludedAccountDelete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeExcludedTool, transport.ExchangeCount);
        using var currencyGroup = await Call(writeToken, "create_group", "{\"name\":\"Home\",\"currency\":\"EUR\"}");
        Assert.DoesNotContain("\"isError\":true", await currencyGroup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("\"currency\":\"EUR\"", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        using var defaultCurrency = await Call(writeToken, "set_group_default_currency", "{\"groupId\":\"" + group + "\",\"currency\":\"EUR\"}");
        Assert.DoesNotContain("\"isError\":true", await defaultCurrency.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("currency", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EUR", transport.ApiBody);
        using var currencyEdit = await Call(writeToken, "update_transaction", "{\"transactionId\":\"" + group + "\",\"edit\":{\"currency\":\"EUR\"}}");
        Assert.DoesNotContain("\"isError\":true", await currencyEdit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("currency", transport.ApiBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EUR", transport.ApiBody);
        transport.RefuseApi = true;
        using var forbiddenStaff = await Call(subjectToken, "staff_team");
        Assert.Contains("\"isError\":true", await forbiddenStaff.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/staff/team", transport.ApiPath);
        Assert.Equal("api-user-token", transport.ApiBearer);
        using var forbiddenReceipt = await Call(subjectToken, "download_expense_receipt", "{\"expenseId\":\"" + group + "\",\"attachmentId\":\"" + group + "\"}");
        Assert.Contains("\"isError\":true", await forbiddenReceipt.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var forbidden = await Call(subjectToken, "list_groups");
        Assert.Contains("\"isError\":true", await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeTransport
    {
        public int ExchangeCount { get; private set; }
        public string? SubjectToken { get; private set; }
        public string? ApiBearer { get; private set; }
        public string ApiPath { get; private set; } = "";
        public List<string> ApiCalls { get; } = [];
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
                owner.ApiCalls.Add(owner.ApiPath);
                owner.ApiMethod = request.Method;
                owner.ApiBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                var filing = owner.ApiPath.EndsWith("/file", StringComparison.Ordinal);
                if (!owner.RefuseApi && owner.ApiPath.StartsWith("/support-reports", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }
                if (!owner.RefuseApi && owner.ApiPath.Contains("/receipt-attachments", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(request.Method == HttpMethod.Get ? "hello" : "{}", Encoding.UTF8,
                            request.Method == HttpMethod.Get ? "image/png" : "application/json")
                    };
                }
                return new HttpResponseMessage(owner.RefuseApi ? HttpStatusCode.Forbidden : filing ? HttpStatusCode.Created : HttpStatusCode.OK)
                { Content = new StringContent(owner.RefuseApi ? "{}" : owner.ApiPath.Contains("/household-month") ? "{\"year\":2026,\"month\":3,\"timeZone\":\"UTC\",\"from\":\"2026-03-01T00:00:00Z\",\"until\":\"2026-04-01T00:00:00Z\",\"spending\":{\"categories\":[]},\"exposure\":[]}" : owner.ApiPath.Contains("/transactions/by-category") ? "{\"categories\":[]}" : owner.ApiPath.Contains("/receipts/inbox/") && !owner.ApiPath.EndsWith("/matches") ? "{\"id\":\"00000000-0000-0000-0000-000000000001\",\"currency\":\"USD\",\"receipt\":{},\"attachments\":[]}" : owner.ApiPath.Contains("/activity") ? "{\"items\":[],\"page\":2,\"pageSize\":100,\"totalCount\":0}" : owner.ApiPath.Contains("preview") ? "{\"splits\":[],\"ruleName\":null}" : owner.ApiPath.Contains("example-token") || filing || request.Method == HttpMethod.Patch || (request.Method == HttpMethod.Post && owner.ApiPath == "/groups") ? "{}" : "[]", Encoding.UTF8, "application/json") };
            }
        }
    }
}
