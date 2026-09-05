using System.Net;
using System.Text;
using System.Text.Json;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GroupSplit.Seeder.Test.Keycloak;

/// <summary>
/// The calls the client makes, over a stubbed transport. The one worth pinning is the create:
/// it has to go through partial import, because <c>POST /users</c> discards the id it is given
/// and mints a new one — which would leave every seeded account with a subject that matches
/// nothing in the app database, and is exactly the defect this seeder exists to prevent.
/// </summary>
public class KeycloakAdminClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SeededId = "9F8E7D6C-5B4A-3C2D-1E0F-1234567890AB";

    private static KeycloakAdminClient ClientFor(StubHandler handler, KeycloakSeedOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://keycloak.test/") };

        return new KeycloakAdminClient(
            http,
            MsOptions.Create(options ?? new KeycloakSeedOptions { AdminUser = "admin", AdminPassword = "secret" }),
            NullLogger<KeycloakAdminClient>.Instance);
    }

    private static KeycloakUser AUser() => new()
    {
        Id = SeededId,
        Username = "anabel@test.com",
        Email = "anabel@test.com",
        FirstName = "Anabel",
        Credentials = [new KeycloakCredential { Value = "GroupSplit123!" }]
    };

    [Fact]
    public async Task Signing_in_uses_the_password_grant_against_the_admin_realm()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");

        await ClientFor(handler).SignInAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("grant_type=password", request.Body);
        Assert.Contains("client_id=admin-cli", request.Body);
        Assert.Contains("username=admin", request.Body);
    }

    /// <summary>The trap, pinned: a plain user create would silently renumber the account.</summary>
    [Fact]
    public async Task Creating_an_account_goes_through_partial_import_so_the_id_survives()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/partialImport", """{"added":1,"skipped":0,"overwritten":0}""");

        await ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken);

        var import = handler.Requests.Last();
        Assert.EndsWith("/admin/realms/group-split/partialImport", import.Uri);
        Assert.DoesNotContain("/admin/realms/group-split/users", import.Uri);

        using var body = JsonDocument.Parse(import.Body);
        Assert.Equal("FAIL", body.RootElement.GetProperty("ifResourceExists").GetString());

        var user = body.RootElement.GetProperty("users").EnumerateArray().Single();
        Assert.Equal(SeededId, user.GetProperty("id").GetString());
        Assert.Equal("anabel@test.com", user.GetProperty("username").GetString());
        Assert.True(user.GetProperty("emailVerified").GetBoolean());
        Assert.False(user.GetProperty("credentials").EnumerateArray().Single().GetProperty("temporary").GetBoolean());
    }

    /// <summary>
    /// A partial import that adds nothing still answers 200, so the counts are the only place
    /// a silent no-op would show. Left unchecked, the seeder would report success over a realm
    /// that gained no accounts.
    /// </summary>
    [Fact]
    public async Task An_import_that_adds_nothing_is_an_error_rather_than_a_success()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/partialImport", """{"added":0,"skipped":1,"overwritten":0}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken));

        Assert.Contains("without creating it", exception.Message);
    }

    [Fact]
    public async Task A_lookup_by_email_is_exact_so_it_cannot_match_a_longer_address()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/users", """[{"id":"an-id","email":"omar@test.com"}]""");

        var found = await ClientFor(handler)
            .FindByEmailAsync("omar@test.com", TestContext.Current.CancellationToken);

        Assert.Equal("an-id", found?.Id);
        Assert.Contains("exact=true", handler.Requests.Last().Uri);
        Assert.Contains("email=omar%40test.com", handler.Requests.Last().Uri);
    }

    [Fact]
    public async Task A_missing_user_is_null_rather_than_an_error()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond($"/admin/realms/group-split/users/{SeededId}", string.Empty, HttpStatusCode.NotFound);

        Assert.Null(await ClientFor(handler).FindByIdAsync(SeededId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Keycloak puts the actual reason in the body -- a password policy violation, say -- and
    /// the status alone never explains it, so the message has to carry both.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_reported_with_what_keycloak_said()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/partialImport",
            """{"errorMessage":"Password policy not met"}""", HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken));

        Assert.Contains("anabel@test.com", exception.Message);
        Assert.Contains("400", exception.Message);
        Assert.Contains("Password policy not met", exception.Message);
    }

    [Fact]
    public async Task Without_credentials_nothing_is_sent()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientFor(handler, new KeycloakSeedOptions()).SignInAsync(TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_token_is_reused_across_calls_rather_than_fetched_each_time()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/users", "[]");

        var client = ClientFor(handler);
        await client.SignInAsync(TestContext.Current.CancellationToken);
        await client.FindByEmailAsync("omar@test.com", TestContext.Current.CancellationToken);
        await client.FindByEmailAsync("daniel@test.com", TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests, request => request.Uri.Contains("openid-connect/token"));
        Assert.All(handler.Requests.Where(request => request.Uri.Contains("/admin/")),
            request => Assert.Equal("Bearer a-token", request.Authorization));
    }

    // ---- Stub transport ----------------------------------------------------------------

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string Path, string Body, HttpStatusCode Status)> _responses = [];

        public List<RecordedRequest> Requests { get; } = [];

        public void Respond(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses.Add((path, body, status));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(
                request.Method, uri, body, request.Headers.Authorization?.ToString()));

            var match = _responses.FirstOrDefault(response =>
                request.RequestUri.AbsolutePath.StartsWith(response.Path, StringComparison.Ordinal));

            if (match.Path is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            return new HttpResponseMessage(match.Status)
            {
                Content = new StringContent(match.Body, Encoding.UTF8, "application/json")
            };
        }
    }
}
