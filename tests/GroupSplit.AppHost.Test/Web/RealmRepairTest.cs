using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Npgsql;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// The realm worker repairing a realm that was imported before #312 was fixed, against the
/// real Keycloak admin API rather than a stub.
/// </summary>
/// <remarks>
/// <para>
/// Every realm the tests start with is already right: in CI it was just imported from the
/// fixed file, and locally the worker fixed it at start-up. So this puts the old shape back
/// by hand -- the profile review in front of the link, the lookup in a nested subflow, no
/// claim filter, email not trusted -- and runs the worker again.
/// </para>
/// <para>
/// In the same collection as <see cref="GoogleFirstSignInTest"/>, which signs in through the
/// flow this test takes apart.
/// </para>
/// </remarks>
[Collection(GoogleFirstBrokerLogin.Collection)]
public class RealmRepairTest(AppHostFixture appHost)
{
    private const string Realm = "group-split";

    private const string Flow = "link only first broker login";

    private static readonly string FlowExecutions =
        $"admin/realms/{Realm}/authentication/flows/{Uri.EscapeDataString(Flow)}/executions";

    private const string Google = $"admin/realms/{Realm}/identity-provider/instances/google";

    private KeycloakAdmin Admin => appHost.Keycloak;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 300_000)]
    public async Task The_worker_takes_the_profile_form_out_of_a_realm_imported_before_the_fix()
    {
        var secret = await GoogleClientSecretAsync();
        var enabled = (await Admin.GetAsync(Google, Ct)).GetProperty("enabled").GetBoolean();
        var subflowId = await PutTheOldFlowBackAsync();
        await PutTheOldProviderBackAsync();

        await RunTheWorkerAsync();

        Assert.Equal(
            ["0 idp-detect-existing-broker-user REQUIRED", "0 idp-auto-link REQUIRED"],
            await StepsAsync());
        Assert.False(
            await Admin.ExistsAsync($"admin/realms/{Realm}/authentication/flows/{subflowId}", Ct),
            "The old lookup subflow outlived the rebuild.");

        var google = await Admin.GetAsync(Google, Ct);
        var config = google.GetProperty("config");
        Assert.True(google.GetProperty("trustEmail").GetBoolean());
        Assert.Equal(Flow, google.GetProperty("firstBrokerLoginFlowAlias").GetString());
        Assert.Equal("true", config.GetProperty("filteredByClaim").GetString());
        Assert.Equal("email_verified", config.GetProperty("claimFilterName").GetString());
        Assert.Equal("true", config.GetProperty("claimFilterValue").GetString());

        // What the worker must not touch. The secret only ever reaches it masked, so it is read
        // out of Keycloak's own table: Keycloak keeping the stored one when the mask is written
        // back is the only thing between this worker and a Google sign-in that stops working.
        Assert.Equal(enabled, google.GetProperty("enabled").GetBoolean());
        Assert.Equal(secret, await GoogleClientSecretAsync());
    }

    /// <summary>
    /// Rebuilding mints new executions, so the same ids after a second run mean the worker saw
    /// nothing to change -- on the real admin API, which returns the flow in its own shape.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task A_second_run_over_a_repaired_realm_leaves_the_flow_alone()
    {
        await PutTheOldFlowBackAsync();
        await RunTheWorkerAsync();

        // Otherwise a worker that never touched the flow would pass as well.
        Assert.Equal(
            ["0 idp-detect-existing-broker-user REQUIRED", "0 idp-auto-link REQUIRED"],
            await StepsAsync());

        var repaired = await ExecutionIdsAsync();

        await RunTheWorkerAsync();

        Assert.Equal(repaired, await ExecutionIdsAsync());
    }

    /// <summary>
    /// The flow as realms.json described it until #312: the profile review, then a subflow
    /// that finds the account and links it.
    /// </summary>
    /// <returns>The subflow's id, to show the rebuild removed it as well.</returns>
    private async Task<string> PutTheOldFlowBackAsync()
    {
        foreach (var execution in (await Admin.GetAsync(FlowExecutions, Ct)).EnumerateArray()
                     .Where(execution => execution.GetProperty("level").GetInt32() == 0))
        {
            await Admin.SendAsync(HttpMethod.Delete,
                $"admin/realms/{Realm}/authentication/executions/{execution.GetProperty("id").GetString()}", null, Ct);
        }

        // Unique, so a run that failed half way cannot leave a name behind that the next one
        // collides with.
        var subflow = $"{Flow} user creation or linking {Guid.NewGuid():N}"[..80];

        await Admin.SendAsync(HttpMethod.Post, $"{FlowExecutions}/execution", new { provider = "idp-review-profile" }, Ct);
        await Admin.SendAsync(HttpMethod.Post, $"{FlowExecutions}/flow",
            new { alias = subflow, type = "basic-flow", description = "Put back by RealmRepairTest." }, Ct);

        var subflowExecutions = $"admin/realms/{Realm}/authentication/flows/{Uri.EscapeDataString(subflow)}/executions";
        await Admin.SendAsync(HttpMethod.Post, $"{subflowExecutions}/execution", new { provider = "idp-detect-existing-broker-user" }, Ct);
        await Admin.SendAsync(HttpMethod.Post, $"{subflowExecutions}/execution", new { provider = "idp-auto-link" }, Ct);

        foreach (var execution in (await Admin.GetAsync(FlowExecutions, Ct)).EnumerateArray())
        {
            var required = JsonNode.Parse(execution.GetRawText())!.AsObject();
            required["requirement"] = "REQUIRED";

            await Admin.SendAsync(HttpMethod.Put, FlowExecutions, required, Ct);
        }

        Assert.Equal(
            [
                "0 idp-review-profile REQUIRED",
                $"0 {subflow} REQUIRED",
                "1 idp-detect-existing-broker-user REQUIRED",
                "1 idp-auto-link REQUIRED"
            ],
            await StepsAsync());

        return (await Admin.GetAsync(FlowExecutions, Ct)).EnumerateArray()
            .Single(execution => execution.TryGetProperty("authenticationFlow", out var isFlow) && isFlow.GetBoolean())
            .GetProperty("flowId").GetString()!;
    }

    /// <summary>The provider as every realm imported before #312 had it, and with email not trusted.</summary>
    private async Task PutTheOldProviderBackAsync()
    {
        var google = JsonNode.Parse((await Admin.GetAsync(Google, Ct)).GetRawText())!.AsObject();
        var config = google["config"]!.AsObject();

        config.Remove("filteredByClaim");
        config.Remove("claimFilterName");
        config.Remove("claimFilterValue");
        google["trustEmail"] = false;

        await Admin.SendAsync(HttpMethod.Put, Google, google, Ct);

        Assert.False((await Admin.GetAsync(Google, Ct)).GetProperty("config").TryGetProperty("filteredByClaim", out _));
    }

    private Task RunTheWorkerAsync() => appHost.RunRealmWorkerAsync();

    private async Task<string[]> StepsAsync() =>
        (await Admin.GetAsync(FlowExecutions, Ct)).EnumerateArray()
        .Select(execution =>
            $"{execution.GetProperty("level").GetInt32()} "
            + $"{(execution.TryGetProperty("providerId", out var provider) ? provider.GetString() : execution.GetProperty("displayName").GetString())} "
            + execution.GetProperty("requirement").GetString())
        .ToArray();

    private async Task<string[]> ExecutionIdsAsync() =>
        (await Admin.GetAsync(FlowExecutions, Ct)).EnumerateArray()
        .Select(execution => execution.GetProperty("id").GetString()!)
        .ToArray();

    /// <summary>The Google provider's client secret as Keycloak stores it, which no admin call returns.</summary>
    private async Task<string?> GoogleClientSecretAsync()
    {
        await using var connection = new NpgsqlConnection(
            await appHost.Application.GetConnectionStringAsync("keycloak-db", Ct));
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            """
            select config.value
            from identity_provider_config config
            join identity_provider provider on provider.internal_id = config.identity_provider_id
            join realm on realm.id = provider.realm_id
            where realm.name = @realm and provider.provider_alias = 'google' and config.name = 'clientSecret'
            """,
            connection);
        command.Parameters.AddWithValue("realm", Realm);

        var secret = await command.ExecuteScalarAsync(Ct) as string;

        Assert.False(string.IsNullOrEmpty(secret), "The realm's Google provider has no stored client secret to compare.");

        return secret;
    }
}

/// <summary>
/// The tests that sign in through, or take apart, the Google first broker login flow, and
/// the password sign-ins a re-pointed browser flow would break. They share one realm, so
/// they run one at a time.
/// </summary>
/// <remarks>
/// And with nothing else running: every test that signs in with a password shares the realm
/// too. <c>ReturnUrlRedirectTest</c> sat outside this collection and timed out on Keycloak's
/// password step whenever its sign-in landed while <c>RealmBindingsAndCliClientTest</c> had
/// the browser flow re-pointed; <c>AccountDeletionTest</c> and <c>KeycloakEventsTest</c> sign
/// in the same way. Joining each of them here would leave the next sign-in test to remember
/// the same; running the collection on its own covers them all.
/// </remarks>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class GoogleFirstBrokerLogin
{
    public const string Collection = "Google first broker login";
}
