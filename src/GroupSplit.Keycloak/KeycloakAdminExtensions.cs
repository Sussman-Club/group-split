using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Keycloak;

public static class KeycloakAdminExtensions
{
    /// <summary>
    /// The name the AppHost's <c>WithReference(keycloak)</c> publishes the endpoint under.
    /// <c>https+http</c> takes whichever scheme the resource exposes, so this works whether
    /// Keycloak is served over TLS locally or not.
    /// </summary>
    private const string KeycloakEndpoint = "https+http://keycloak";

    extension(IHostApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddKeycloakAdminClient()
        {
            builder.Services.Configure<KeycloakAdminOptions>(
                builder.Configuration.GetSection(KeycloakAdminOptions.SectionName));

            builder.Services.AddServiceDiscovery();

            builder.Services
                .AddHttpClient<KeycloakAdminClient>(client =>
                {
                    // A trailing slash, so the relative paths the client builds append rather
                    // than replace the last segment.
                    client.BaseAddress = new Uri(KeycloakEndpoint + "/");
                })
                .AddServiceDiscovery();

            builder.Services.AddTransient<IKeycloakAdminClient>(
                services => services.GetRequiredService<KeycloakAdminClient>());
            builder.Services.AddTransient<IKeycloakRealmAdminClient>(
                services => services.GetRequiredService<KeycloakAdminClient>());

            return builder;
        }
    }
}
