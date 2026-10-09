namespace GroupSplit.AppHost.Extensions;

/// <summary>
/// The secret of <c>account-deletion</c>, the confidential client the API deletes a deleted
/// account's Keycloak login through, and the three places that need it.
/// </summary>
/// <remarks>
/// <para>
/// Keycloak reads it from <see cref="SecretVariable"/> when it imports a fresh realm; the realm
/// worker reads the same variable and holds the client to it on a realm that already exists,
/// creating the client there if it has to; and the API signs in with it. One parameter feeds
/// all three, so they cannot disagree for longer than it takes the worker to run -- and the API
/// waits for the worker.
/// </para>
/// <para>
/// The client is narrow on purpose: its service account holds <c>manage-users</c> in the app's
/// realm and nothing else, which is what deleting a user and ending their sessions needs. The
/// API never sees the master realm's admin.
/// </para>
/// </remarks>
public static class KeycloakAccountDeletionExtensions
{
    /// <summary>
    /// Named so its Compose variable is <see cref="SecretVariable"/>. Deployed, the realm file
    /// travels as an inline Compose config and Compose interpolates its <c>${...}</c> from its
    /// own variables, so the two spellings have to agree. See
    /// <see cref="KeycloakRealmResourceBuilderExtensions.WebHostnameVariable"/>.
    /// </summary>
    public const string SecretParameterName = "keycloak-account-deletion-secret";

    /// <summary>The placeholder <c>realms.json</c> takes the client's secret from.</summary>
    public const string SecretVariable = "KEYCLOAK_ACCOUNT_DELETION_SECRET";

    /// <summary>The client's id, as <c>realms.json</c> names it.</summary>
    private const string ClientId = "account-deletion";

    /// <summary>The realm the client belongs to, which is also the one the API's token comes from.</summary>
    private const string Realm = "group-split";

    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Generated and kept in the AppHost's user secrets locally, where nobody should have to
        /// configure anything; a deploy secret otherwise. Aspire would generate a new value on
        /// every publish, which is harmless only because the worker would rewrite the client to
        /// match -- but a value the operator holds is one they can rotate on purpose.
        /// </summary>
        public IResourceBuilder<ParameterResource> AddAccountDeletionSecret()
        {
            var parameter = builder.ExecutionContext.IsRunMode
                ? builder.AddParameter(
                    SecretParameterName,
                    new GenerateParameterDefault { MinLength = 32, Special = false },
                    secret: true,
                    persist: true)
                : builder.AddParameter(SecretParameterName, secret: true);

            return parameter.WithDescription(
                "Secret of the account-deletion client in the group-split realm, which the API "
                + "deletes a deleted account's Keycloak login through. Any long random string.");
        }
    }

    extension<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
    {
        /// <summary>
        /// For Keycloak's first-boot import and for the realm worker: the variable the realm
        /// file's placeholder names.
        /// </summary>
        public IResourceBuilder<T> WithAccountDeletionSecret(IResourceBuilder<ParameterResource> secret) =>
            resource.WithEnvironment(SecretVariable, secret);

        /// <summary>
        /// For the API: sign in to Keycloak's admin API as the client's service account, with the
        /// client-credentials grant against the app's realm.
        /// </summary>
        public IResourceBuilder<T> WithAccountDeletionClient(IResourceBuilder<ParameterResource> secret) =>
            resource
                .WithEnvironment("Keycloak__AdminRealm", Realm)
                .WithEnvironment("Keycloak__AdminClientId", ClientId)
                .WithEnvironment("Keycloak__AdminClientSecret", secret);
    }
}
