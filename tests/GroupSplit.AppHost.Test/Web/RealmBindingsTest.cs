using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// The realm's flow bindings, on the running realm and after the
/// realm worker has repaired them, against the real Keycloak admin API.
/// </summary>
/// <remarks>
/// In the collection that runs the realm worker again, so two tests never take the realm
/// apart, or rerun the worker, at the same time.
/// </remarks>
[Collection(GoogleFirstBrokerLogin.Collection)]
public class RealmBindingsTest(AppHostFixture appHost)
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
    /// makes while the binding is wrong still works. One that starts under one binding and
    /// is posted under the other does not, which is why SignedInSessionTest shares the
    /// collection.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task The_worker_points_a_re_pointed_browser_flow_back()
    {
        var copy = $"browser copy {Guid.NewGuid():N}";

        await Admin.SendAsync(HttpMethod.Post, $"admin/realms/{Realm}/authentication/flows/browser/copy",
            new
            {
                newName = copy
            }, Ct);

        try
        {
            await Admin.SendAsync(HttpMethod.Put, $"admin/realms/{Realm}", new
            {
                realm = Realm,
                browserFlow = copy
            }, Ct);
            Assert.Equal(copy, (await Admin.GetAsync($"admin/realms/{Realm}", Ct)).GetProperty("browserFlow").GetString());

            await appHost.RunRealmWorkerAsync();

            Assert.Equal("browser",
                (await Admin.GetAsync($"admin/realms/{Realm}", Ct)).GetProperty("browserFlow").GetString());
        }
        finally
        {
            await Admin.SendAsync(HttpMethod.Put, $"admin/realms/{Realm}", new
            {
                realm = Realm,
                browserFlow = "browser"
            },
                CancellationToken.None);

            var flows = await Admin.GetAsync($"admin/realms/{Realm}/authentication/flows", CancellationToken.None);
            var id = flows.EnumerateArray().Single(flow => flow.GetProperty("alias").GetString() == copy)
                .GetProperty("id").GetString();

            await Admin.SendAsync(HttpMethod.Delete, $"admin/realms/{Realm}/authentication/flows/{id}", null,
                CancellationToken.None);
        }
    }

}
