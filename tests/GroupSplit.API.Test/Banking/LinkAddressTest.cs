using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The two addresses the bank routes hand out: where a provider is told to deliver webhooks,
/// and where a connection that was just linked says it lives.
/// </summary>
/// <remarks>
/// The webhook address is the one worth a test of its own. It is given to somebody else's
/// server as the place to deliver a person's bank activity, and it used to be read off the
/// request -- which means off a header the caller writes. These say it comes from
/// configuration instead, and that a caller cannot talk this deployment into naming an
/// address of their choosing.
/// </remarks>
public class LinkAddressTest
{
    private const string Origin = "https://groupsplit.example.test";

    private readonly FakeBankConnector _bank = new();

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
    /// The point of configuring it. A caller writes the Host header, so an address derived
    /// from it is an address the caller chose -- and this one is handed to a provider that
    /// will deliver a person's bank activity to it.
    /// </summary>
    [Fact]
    public async Task The_host_the_caller_sent_is_not_what_the_provider_is_told()
    {
        await using var host = await StartAsync(Origin);

        var asked = await WebhookUrlAsync(host, "https://somebody-elses-server.test");

        Assert.Equal($"{Origin}/webhooks/{FakeBankConnector.Name}", asked);
    }

    /// <summary>
    /// With nothing configured the request stands in, which is the development posture: the
    /// API is reached directly there, and no provider could deliver to it whatever it was
    /// told.
    /// </summary>
    [Fact]
    public async Task With_no_configured_origin_the_request_stands_in()
    {
        await using var host = await StartAsync(origin: null);

        var asked = await WebhookUrlAsync(host, "https://localhost:7443");

        Assert.Equal($"https://localhost:7443/webhooks/{FakeBankConnector.Name}", asked);
    }

    /// <summary>
    /// Providers refuse a plain-HTTP webhook address, so a link call carrying one would be
    /// refused outright. No address is the better answer: linking works and nothing is told
    /// where to call back.
    /// </summary>
    [Fact]
    public async Task Over_plain_http_with_nothing_configured_the_provider_is_told_no_address()
    {
        await using var host = await StartAsync(origin: null);

        Assert.Null(await WebhookUrlAsync(host));
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
            services.Configure<BankingOptions>(options =>
            {
                options.Provider = FakeBankConnector.Name;
                options.PublicOrigin = origin;
            });
        });

    /// <summary>
    /// The webhook address the provider was handed, asked for over HTTP so the whole
    /// endpoint is in the way. <paramref name="origin"/> is the origin the request is
    /// addressed to, which the test host passes through as the request's scheme and host.
    /// </summary>
    private async Task<string?> WebhookUrlAsync(ApiEndpointHost host, string? origin = null)
    {
        var response = await host.Client.PostAsJsonAsync(
            $"{origin}/bank-connections/link-token", new LinkTokenRequest(), Ct);

        response.EnsureSuccessStatusCode();

        return Assert.Single(_bank.LinkSessionsSeen).WebhookUrl;
    }
}
