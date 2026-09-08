using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The two addresses the bank routes hand out: where a provider is told to deliver webhooks,
/// and where a connection that was just linked says it lives.
/// </summary>
/// <remarks>
/// The webhook address is the one worth a test of its own. It is given to somebody else's
/// server as the place to deliver a person's bank activity, so where it comes from matters:
/// configuration, never the request, whose host the caller writes. And the provider it names
/// has to be the one that opened the session, which in update mode is not this deployment's
/// default.
/// </remarks>
public class LinkAddressTest
{
    private const string Origin = "https://groupsplit.example.test";

    /// <summary>A second provider, so update mode has one to disagree with the default.</summary>
    private const string OtherProvider = "other-bank";

    private readonly FakeBankConnector _bank = new();

    private readonly FakeBankConnector _other = new(OtherProvider);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_provider_is_told_the_configured_origin()
    {
        await using var host = await StartAsync(Origin);

        Assert.Equal($"{Origin}/webhooks/{FakeBankConnector.Name}", await WebhookUrlAsync(host));
    }

    /// <summary>
    /// A deployment writes the origin by hand, and a trailing slash is the ordinary way to
    /// write one. It must not arrive at the provider as a doubled separator.
    /// </summary>
    [Fact]
    public async Task A_trailing_slash_on_the_configured_origin_is_not_doubled()
    {
        await using var host = await StartAsync($"{Origin}/");

        Assert.Equal($"{Origin}/webhooks/{FakeBankConnector.Name}", await WebhookUrlAsync(host));
    }

    /// <summary>
    /// The point of taking it from configuration. A caller writes the Host header, so an
    /// address derived from it is an address the caller chose -- and this one is handed to a
    /// provider that will deliver a person's bank activity to it.
    /// </summary>
    [Fact]
    public async Task The_host_the_caller_sent_is_not_what_the_provider_is_told()
    {
        await using var host = await StartAsync(Origin);

        var asked = await WebhookUrlAsync(host, "https://somebody-elses-server.test");

        Assert.Equal($"{Origin}/webhooks/{FakeBankConnector.Name}", asked);
    }

    /// <summary>
    /// Locally there is nothing an outside provider could deliver to, so there is no address
    /// to give. Linking still works; a sync runs on linking, on demand and nightly.
    /// </summary>
    [Fact]
    public async Task With_no_configured_origin_the_provider_is_told_no_address()
    {
        await using var host = await StartAsync(origin: null);

        Assert.Null(await WebhookUrlAsync(host, "https://localhost:7443"));
    }

    /// <summary>
    /// Update mode repairs an existing connection, and it is that connection's provider that
    /// opens the session -- so the path has to name that provider, not the deployment default.
    /// Naming the wrong one hands its webhooks to a verifier that refuses them, and the
    /// connection never hears from its bank again.
    /// </summary>
    [Fact]
    public async Task Update_mode_names_the_connections_own_provider()
    {
        await using var host = await StartAsync(Origin);

        var connection = await ConnectionAsync(host, OtherProvider);

        var response = await host.Client.PostAsJsonAsync("/bank-connections/link-token",
            new LinkTokenRequest { ConnectionId = connection.Id }, Ct);

        response.EnsureSuccessStatusCode();

        // The default provider was not the one asked, and the one that was asked was told its
        // own path.
        Assert.Empty(_bank.LinkSessionsSeen);
        Assert.Equal($"{Origin}/webhooks/{OtherProvider}",
            Assert.Single(_other.LinkSessionsSeen).WebhookUrl);
    }

    [Fact]
    public async Task A_linked_connection_says_where_it_lives()
    {
        await using var host = await StartAsync(Origin);

        // The sync every link asks for has to have something to read.
        _bank.Answer("cursor-one");

        var response = await host.Client.PostAsJsonAsync(
            "/bank-connections", new CreateBankConnectionRequest { PublicToken = "public-token" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var connection = await response.Content.ReadFromJsonAsync<BankConnectionResponse>(Ct);

        Assert.NotNull(connection);
        Assert.Equal($"/bank-connections/{connection.Id}", response.Headers.Location?.ToString());
    }

    // ---- setup ---------------------------------------------------------------------------

    private Task<ApiEndpointHost> StartAsync(string? origin) =>
        ApiEndpointHost.StartAsync(services =>
        {
            services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
            services.AddKeyedSingleton<IBankConnector>(OtherProvider, _other);
            services.Configure<BankingOptions>(options =>
            {
                options.Provider = FakeBankConnector.Name;
                options.PublicOrigin = origin;
            });
        });

    /// <summary>
    /// The webhook address the provider was handed, asked for over HTTP so the whole endpoint
    /// is in the way. <paramref name="origin"/> is the origin the request is addressed to,
    /// which the test host passes through as the request's scheme and host.
    /// </summary>
    private async Task<string?> WebhookUrlAsync(ApiEndpointHost host, string? origin = null)
    {
        var response = await host.Client.PostAsJsonAsync(
            $"{origin}/bank-connections/link-token", new LinkTokenRequest(), Ct);

        response.EnsureSuccessStatusCode();

        return Assert.Single(_bank.LinkSessionsSeen).WebhookUrl;
    }

    /// <summary>A stored connection of the host's own user, linked through one provider.</summary>
    private async Task<BankConnection> ConnectionAsync(ApiEndpointHost host, string provider)
    {
        // Provisions the caller, so the connection below belongs to somebody the API knows.
        await host.Client.GetAsync("/bank-connections", Ct);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

        var connection = new BankConnection
        {
            UserId = (await dbContext.Set<Data.Entities.User>()
                .OrderBy(user => user.Id)
                .FirstAsync(Ct)).Id,
            Provider = provider,
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = protector.Protect(FakeBankConnector.AccessToken),
            LinkedAt = DateTimeOffset.UtcNow
        };

        dbContext.Add(connection);
        await dbContext.SaveChangesAsync(Ct);

        return connection;
    }
}
