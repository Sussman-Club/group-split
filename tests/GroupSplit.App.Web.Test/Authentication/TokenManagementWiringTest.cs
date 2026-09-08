using Duende.AccessTokenManagement.OpenIdConnect;
using GroupSplit.App.Web.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// That the token-management graph is actually wired, and wired the way round that makes it
/// reachable from a circuit.
/// </summary>
/// <remarks>
/// Worth its own test because every part of this fails silently in the direction that
/// matters. The library registers its own store and user accessor with <c>TryAdd</c>, so
/// ours only take effect if they are registered afterwards -- get the order wrong and the
/// app still starts, still signs people in, and goes back to reading tokens off an
/// <c>HttpContext</c> a circuit does not usefully have.
/// </remarks>
public class TokenManagementWiringTest
{
    /// <summary>
    /// The registration that fixes the deployed render mode. If this ever comes back as the
    /// library's <c>AuthenticationSessionUserAccessTokenStore</c>, tokens are being kept in
    /// the ticket again and a circuit cannot refresh them.
    /// </summary>
    [Fact]
    public void The_token_store_is_ours_and_not_the_one_that_needs_an_http_context()
    {
        using var services = Build();

        Assert.IsType<ServerSideTokenStore>(services.GetRequiredService<IUserTokenStore>());
    }

    /// <summary>
    /// The other half. The user accessor is what finds the principal, and the library's
    /// default finds it on <c>HttpContext</c> -- which inside a circuit is the long-lived
    /// one the connection was opened over, carrying the principal as it was at page load.
    /// </summary>
    [Fact]
    public void The_user_accessor_is_the_one_that_can_read_a_circuit()
    {
        using var services = Build();

        Assert.Equal(
            "BlazorServerUserAccessor",
            services.GetRequiredService<IUserAccessor>().GetType().Name);
    }

    [Fact]
    public void The_token_manager_resolves()
    {
        using var services = Build();

        Assert.NotNull(services.GetRequiredService<IUserTokenManager>());
    }

    /// <summary>
    /// What the accessor above reads the principal out of. Registered by the interactive
    /// server render mode rather than by anything here, which is exactly why it is asserted
    /// here: the render mode is chosen by configuration, and the deployment leaves it at the
    /// default.
    /// </summary>
    [Fact]
    public void The_render_mode_the_deployment_uses_supplies_an_authentication_state_provider()
    {
        using var services = Build();

        Assert.NotNull(services.GetService<AuthenticationStateProvider>());
    }

    /// <summary>
    /// The invariant that keeps this a BFF. SaveTokens writes the access and refresh tokens
    /// into the authentication ticket, and the ticket is what the sign-in cookie is made of
    /// -- so leaving it on puts a browser one misconfigured ticket store away from holding
    /// them.
    /// </summary>
    [Fact]
    public void The_sign_in_cookie_is_never_made_out_of_tokens()
    {
        using var services = Build();

        var options = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<
                Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);

        Assert.False(options.SaveTokens, "tokens belong in the store, not in the ticket");
    }

    /// <summary>
    /// The handler is built the way <c>ClientOptionsSetter</c> builds it, out of the scope
    /// <c>IHttpClientFactory</c> hands a handler chain -- which is not the request's scope
    /// and outlives it by the handler lifetime.
    /// </summary>
    /// <remarks>
    /// That it resolves at all is worth asserting, because the alternative fails at the
    /// first call to the API rather than at startup. That it is <em>safe</em> to resolve
    /// there rests on something this test cannot see: neither the user accessor nor the
    /// token store holds per-request state, and the accessor reads the circuit or request it
    /// is being called on from an ambient <c>AsyncLocal</c> at call time. Give either of them
    /// a dependency that is genuinely per-request and this arrangement starts handing one
    /// person's token to another.
    /// </remarks>
    [Fact]
    public void The_api_handler_can_be_built_from_a_handler_chain_scope()
    {
        using var services = Build();
        using var handlerChainScope = services.CreateScope();

        Assert.NotNull(
            ActivatorUtilities.GetServiceOrCreateInstance<GroupSplit.App.Web.Services.AuthDelegatingHandler>(
                handlerChainScope.ServiceProvider));
    }

    /// <summary>
    /// Boots the app's own authentication and render-mode wiring, in the configuration a
    /// deployment gets: production, and no RenderMode set, which is what leaves it on
    /// interactive server.
    /// </summary>
    private static ServiceProvider Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Keycloak:Authority"] = "https://keycloak.test/realms/group-split"
        });

        builder.WebHost.UseTestServer();

        builder.AddGroupSplitAuthentication();
        builder.Services.AddAuthorizationBuilder();

        RenderModeConfig.Initialize(
            builder.Configuration.GetValue<RenderModePreference>("RenderMode"));

        builder.Services.AddRazorComponents().AddRenderModeComponents();

        return builder.Services.BuildServiceProvider();
    }
}
