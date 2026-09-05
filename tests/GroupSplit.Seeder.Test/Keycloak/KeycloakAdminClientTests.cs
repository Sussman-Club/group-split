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

    private const string DefaultRole = "default-roles-group-split";

    private const string RealmWithDefaultRole =
        """{"realm":"group-split","defaultRole":{"id":"a-role-id","name":"default-roles-group-split","composite":true,"clientRole":false}}""";

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
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

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
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken));

        Assert.Contains("without creating it", exception.Message);
    }

    /// <summary>
    /// The other half of the partial-import trade-off, pinned. <c>POST /users</c> grants
    /// <c>default-roles-{realm}</c> as a side effect and partial import grants only what the
    /// representation lists, so leaving this out produces an account that signs in to the app
    /// perfectly well and meets nothing but 401s on Keycloak's own account console.
    /// </summary>
    [Fact]
    public async Task Creating_an_account_grants_the_realms_default_role()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/partialImport", """{"added":1,"skipped":0,"overwritten":0}""");
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

        await ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Requests.Last().Body);
        var roles = body.RootElement
            .GetProperty("users").EnumerateArray().Single()
            .GetProperty("realmRoles").EnumerateArray()
            .Select(role => role.GetString());

        Assert.Contains(DefaultRole, roles);
    }

    /// <summary>
    /// Read from the realm rather than assembled as <c>default-roles-{realm}</c>: that is only
    /// Keycloak's naming convention, and a realm may point its default somewhere else.
    /// </summary>
    [Fact]
    public async Task The_default_role_comes_from_the_realm_and_is_read_only_once()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split/partialImport", """{"added":1,"skipped":0,"overwritten":0}""");
        handler.Respond("/admin/realms/group-split",
            """{"defaultRole":{"id":"a-role-id","name":"a-renamed-default","composite":true}}""");

        var client = ClientFor(handler);
        await client.CreateAsync(AUser(), TestContext.Current.CancellationToken);
        await client.CreateAsync(AUser() with { Id = "another-id" }, TestContext.Current.CancellationToken);

        Assert.Contains("a-renamed-default", handler.Requests.Last().Body);
        Assert.Single(handler.Requests, request => request.Uri.EndsWith("/admin/realms/group-split"));
    }

    /// <summary>
    /// For accounts seeded before the role was granted: the realm keeps its users in a volume,
    /// so they outlive the fix and nothing else would ever put them right.
    /// </summary>
    [Fact]
    public async Task An_account_missing_the_default_role_has_it_granted()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond($"/admin/realms/group-split/users/{SeededId}/role-mappings/realm", "[]");
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

        var granted = await ClientFor(handler)
            .EnsureDefaultRoleAsync(SeededId, TestContext.Current.CancellationToken);

        Assert.True(granted);

        var post = handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.EndsWith($"/admin/realms/group-split/users/{SeededId}/role-mappings/realm", post.Uri);

        // Granting takes the whole representation, not a name.
        using var body = JsonDocument.Parse(post.Body);
        var role = body.RootElement.EnumerateArray().Single();
        Assert.Equal(DefaultRole, role.GetProperty("name").GetString());
        Assert.Equal("a-role-id", role.GetProperty("id").GetString());
    }

    /// <summary>The seeder runs on every start of the resource, so reruns must not rewrite.</summary>
    [Fact]
    public async Task An_account_already_holding_the_default_role_is_left_alone()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond($"/admin/realms/group-split/users/{SeededId}/role-mappings/realm",
            """[{"id":"a-role-id","name":"default-roles-group-split"}]""");
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

        var granted = await ClientFor(handler)
            .EnsureDefaultRoleAsync(SeededId, TestContext.Current.CancellationToken);

        Assert.False(granted);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post
            && request.Uri.Contains("role-mappings"));
    }

    [Fact]
    public async Task A_realm_naming_no_default_role_is_an_error_rather_than_a_role_less_account()
    {
        var handler = new StubHandler();
        handler.Respond("/realms/master/protocol/openid-connect/token", """{"access_token":"a-token"}""");
        handler.Respond("/admin/realms/group-split", """{"realm":"group-split"}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientFor(handler).CreateAsync(AUser(), TestContext.Current.CancellationToken));

        Assert.Contains("no default role", exception.Message);
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
        handler.Respond("/admin/realms/group-split", RealmWithDefaultRole);

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
