using System.Text.Json.Nodes;
using GroupSplit.Keycloak;
using GroupSplit.RealmWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Realm;

public sealed class RealmSettingsReconcilerTests
{
    [Fact]
    public async Task Updates_managed_settings_without_replacing_unmanaged_realm_data()
    {
        var current = KeycloakRealmDefinition.Load();
        current["displayName"] = "Old name";
        current["smtpServer"] = new JsonObject { ["host"] = "custom.example" };
        var admin = new StubRealmAdmin(current);

        await new RealmSettingsReconciler(admin, NullLogger.Instance)
            .ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, admin.UpdateCount);
        Assert.Equal("Group Split", admin.RealmData["displayName"]?.GetValue<string>());
        Assert.Equal("custom.example", admin.RealmData["smtpServer"]?["host"]?.GetValue<string>());
    }

    [Fact]
    public async Task Matching_realm_causes_no_update()
    {
        var admin = new StubRealmAdmin(KeycloakRealmDefinition.Load());

        await new RealmSettingsReconciler(admin, NullLogger.Instance)
            .ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, admin.UpdateCount);
    }

    private sealed class StubRealmAdmin(JsonObject realm) : IKeycloakRealmAdminClient
    {
        public string Realm => "group-split";
        public JsonObject RealmData { get; private set; } = realm;
        public int UpdateCount { get; private set; }

        public Task SignInAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JsonObject> GetRealmAsync(CancellationToken ct = default)
            => Task.FromResult((JsonObject)RealmData.DeepClone());

        public Task UpdateRealmAsync(JsonObject updated, CancellationToken ct = default)
        {
            RealmData = (JsonObject)updated.DeepClone();
            UpdateCount++;
            return Task.CompletedTask;
        }

        public Task DeleteRealmAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
