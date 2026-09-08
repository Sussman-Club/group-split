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
/// Linking a bank that is already linked, which is what somebody does when a connection
/// misbehaves and repairing it is not offered.
/// </summary>
/// <remarks>
/// The match on the provider's item id cannot see this: a provider mints a new item on every
/// link, so the second one shares no id with the first. What it shares is the bank and the
/// accounts behind it, and that is what has to be recognised -- or the person collects a
/// duplicate of a bank they already had, every row arrives twice, and an item is spent at
/// the provider for nothing. On a plan whose item allowance is spent once and never
/// returned, that last part does not come back.
/// </remarks>
public class DuplicateLinkTest : IAsyncLifetime
{
    private readonly FakeBankConnector _bank = new();

    private ApiEndpointHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await ApiEndpointHost.StartAsync(services =>
        {
            services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
            services.Configure<BankingOptions>(options => options.Provider = FakeBankConnector.Name);
        });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task Link()
    {
        var response = await _host.Client.PostAsJsonAsync(
            "/bank-connections", new CreateBankConnectionRequest { PublicToken = "public-token" }, Ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task<List<BankConnection>> Connections()
    {
        await using var scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task Linking_the_same_bank_again_moves_the_connection_it_already_had()
    {
        // A page each, so the sync every link asks for has something to read.
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();
        await Link();

        var connection = Assert.Single(await Connections());

        Assert.Equal("item-two", connection.ProviderItemId);

        // One real account behind two items. Matching on the provider's account id would
        // have added a second row here and orphaned the rows filed against the first.
        Assert.Single(connection.Accounts);
        Assert.Equal("acc-1", connection.Accounts.Single().ProviderAccountId);
    }

    /// <summary>
    /// Otherwise it is left running at the provider, counted, with nothing on this side
    /// holding a token that could ever name it again.
    /// </summary>
    [Fact]
    public async Task The_item_that_was_replaced_is_retired_at_the_provider()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();
        await Link();

        Assert.Contains("token-one", _bank.RemovedTokens);
        Assert.DoesNotContain("token-two", _bank.RemovedTokens);
    }

    /// <summary>
    /// The cursor is the replaced item's place in its own stream, and resuming a new item
    /// from it would ask the provider about somewhere it has never been.
    /// </summary>
    [Fact]
    public async Task The_replaced_items_cursor_is_not_carried_over()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item("item-two", "token-two"));

        await Link();
        await Link();

        // Whatever the sync that follows the adoption has since written, the cursor the
        // first item left behind must not be what the new one started from.
        Assert.DoesNotContain("cursor-one", _bank.CursorsSeen.Skip(1));
    }

    /// <summary>
    /// Two genuinely separate logins at one bank -- a personal one and a business one --
    /// have no account in common, and are the second connection the person asked for.
    /// </summary>
    [Fact]
    public async Task Two_logins_at_one_bank_stay_two_connections()
    {
        _bank.Answer("cursor-one").Answer("cursor-two");

        _bank.AnswerExchange(FakeBankConnector.Item("item-one", "token-one"))
            .AnswerExchange(FakeBankConnector.Item(
                "item-two", "token-two", accountId: "acc-9", name: "Business", mask: "9876"));

        await Link();
        await Link();

        Assert.Equal(2, (await Connections()).Count);
        Assert.Empty(_bank.RemovedTokens);
    }
}
