using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// The realm's flow bindings and the <c>cli</c> client, on the running realm and after the
/// realm worker has repaired them, against the real Keycloak admin API.
/// </summary>
/// <remarks>
/// In the collection that runs the realm worker again, so two tests never take the realm
/// apart, or rerun the worker, at the same time.
/// </remarks>
[Collection(GoogleFirstBrokerLogin.Collection)]
public class RealmBindingsAndCliClientTest(AppHostFixture appHost)
{
    private const string Realm = "group-split";

    private KeycloakAdmin Admin => appHost.Keycloak;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The bindings realms.json names, which are Keycloak's built-in flows.</summary>
    public static TheoryData<string, string> Bindings => new()
    {
        { "browserFlow", "browser" },
        { "registrationFlow", "registration" },
        { "directGrantFlow", "direct grant" },
        { "resetCredentialsFlow", "reset credentials" },
        { "clientAuthenticationFlow", "clients" },
        { "firstBrokerLoginFlow", "first broker login" }
    };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Bindings))]
    public async Task The_running_realm_binds_each_kind_of_sign_in_to_the_files_flow(string binding, string flow)
    {
        var realm = await Admin.GetAsync($"admin/realms/{Realm}", Ct);

        Assert.Equal(flow, realm.GetProperty(binding).GetString());
    }

    /// <summary>
    /// Re-pointed at a copy rather than a genuinely weaker flow, so a sign-in another test
    /// makes while the binding is wrong still works.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task The_worker_points_a_re_pointed_browser_flow_back()
    {
        var copy = $"browser copy {Guid.NewGuid():N}";

        await Admin.SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/authentication/flows/browser/copy",
            new { newName = copy }, Ct);

        try
        {
            await Admin.SendAsync(HttpMethod.Put, $"admin/realms/{Realm}", new { realm = Realm, browserFlow = copy }, Ct);
            Assert.Equal(copy, (await Admin.GetAsync($"admin/realms/{Realm}", Ct)).GetProperty("browserFlow").GetString());

            await appHost.RunRealmWorkerAsync();

            Assert.Equal("browser",
                (await Admin.GetAsync($"admin/realms/{Realm}", Ct)).GetProperty("browserFlow").GetString());
        }
        finally
        {
            await Admin.SendAsync(HttpMethod.Put, $"admin/realms/{Realm}", new { realm = Realm, browserFlow = "browser" },
                CancellationToken.None);

            var flows = await Admin.GetAsync($"admin/realms/{Realm}/authentication/flows", CancellationToken.None);
            var id = flows.EnumerateArray().Single(flow => flow.GetProperty("alias").GetString() == copy)
                .GetProperty("id").GetString();

            await Admin.SendAsync(HttpMethod.Delete, $"admin/realms/{Realm}/authentication/flows/{id}", null,
                CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task The_running_cli_client_takes_the_device_grant_alone_and_asks_for_the_api()
    {
        await AssertCliClientAsync();
    }

    /// <summary>
    /// A realm imported before the CLI existed. <c>groupsplit auth login</c> asks Keycloak for a
    /// device code first, so that request working is what the CLI needs.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task The_worker_gives_a_realm_without_the_cli_client_one_that_starts_a_device_sign_in()
    {
        await Admin.SendAsync(HttpMethod.Delete, $"admin/realms/{Realm}/clients/{await CliIdAsync()}", null, Ct);
        Assert.Null(await CliAsync());

        await appHost.RunRealmWorkerAsync();

        await AssertCliClientAsync();

        using var keycloak = new HttpClient { BaseAddress = appHost.Application.GetEndpoint("keycloak", "http") };
        using var response = await keycloak.PostAsync(
            $"realms/{Realm}/protocol/openid-connect/auth/device",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "cli" }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(string.IsNullOrEmpty(payload.RootElement.GetProperty("device_code").GetString()));
    }

    /// <summary>
    /// Every other grant switched on and the audience taken away, by hand. Keycloak's client
    /// update leaves mappers alone, so this also shows the mapper is restored by its own call.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task The_worker_takes_other_grants_away_from_the_cli_client_and_restores_its_audience()
    {
        var cli = JsonNode.Parse((await CliAsync())!.Value.GetRawText())!.AsObject();
        var id = cli["id"]!.GetValue<string>();

        cli["standardFlowEnabled"] = true;
        cli["directAccessGrantsEnabled"] = true;
        cli["attributes"]!["oauth2.device.authorization.grant.enabled"] = "false";
        await Admin.SendAsync(HttpMethod.Put, $"admin/realms/{Realm}/clients/{id}", cli, Ct);

        foreach (var mapper in (await Admin.GetAsync($"admin/realms/{Realm}/clients/{id}/protocol-mappers/models", Ct))
                 .EnumerateArray()
                 .Where(mapper => mapper.GetProperty("name").GetString() == "audience-mapper"))
        {
            await Admin.SendAsync(HttpMethod.Delete,
                $"admin/realms/{Realm}/clients/{id}/protocol-mappers/models/{mapper.GetProperty("id").GetString()}", null, Ct);
        }

        var broken = (await CliAsync())!.Value;
        Assert.True(broken.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.False(broken.TryGetProperty("protocolMappers", out var left)
                     && left.EnumerateArray().Any(mapper => mapper.GetProperty("name").GetString() == "audience-mapper"));

        await appHost.RunRealmWorkerAsync();

        await AssertCliClientAsync();
    }

    private async Task AssertCliClientAsync()
    {
        var cli = await CliAsync();

        Assert.NotNull(cli);
        var client = cli.Value;
        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.False(client.GetProperty("serviceAccountsEnabled").GetBoolean());

        var attributes = client.GetProperty("attributes");
        Assert.Equal("true", attributes.GetProperty("oauth2.device.authorization.grant.enabled").GetString());
        Assert.Equal("false", attributes.GetProperty("oidc.ciba.grant.enabled").GetString());

        var audience = Assert.Single(client.GetProperty("protocolMappers").EnumerateArray(),
            mapper => mapper.GetProperty("name").GetString() == "audience-mapper");
        Assert.Equal("oidc-audience-mapper", audience.GetProperty("protocolMapper").GetString());
        Assert.Equal("api", audience.GetProperty("config").GetProperty("included.client.audience").GetString());
        Assert.Equal("true", audience.GetProperty("config").GetProperty("access.token.claim").GetString());
    }

    private async Task<JsonElement?> CliAsync()
    {
        var matches = await Admin.GetAsync($"admin/realms/{Realm}/clients?clientId=cli", Ct);

        return matches.EnumerateArray().Select(client => (JsonElement?)client).SingleOrDefault();
    }

    private async Task<string> CliIdAsync() =>
        (await CliAsync())?.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("The running realm has no cli client to take away.");
}
