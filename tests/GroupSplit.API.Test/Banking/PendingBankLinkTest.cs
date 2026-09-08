using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The token written down before it can be lost, and the link finished from it afterwards.
/// </summary>
/// <remarks>
/// A provider's item exists from the moment somebody finishes linking, and the token naming
/// it is handed over once. Everything between that and a stored connection is a window in
/// which losing the token loses the item -- live at the provider, counted, and nameable by
/// nothing. These cover the row that closes the window: that it does not outlive a link
/// which succeeded, and that a link which did not get that far is finished from it rather
/// than started again, because starting again spends another item and finishing spends none.
/// </remarks>
public class PendingBankLinkTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions ItemJson = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public async Task A_link_that_succeeds_leaves_nothing_behind()
    {
        _bank.Answer("cursor-one");

        await Link();

        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Set<PendingBankLink>().ToListAsync(Ct));
        Assert.Single(await dbContext.Set<BankConnection>().ToListAsync(Ct));
    }

    /// <summary>
    /// The point of the whole row: what a request could not store, something else can, from
    /// the item alone and without asking anybody to link their bank a second time.
    /// </summary>
    [Fact]
    public async Task An_interrupted_link_is_finished_from_the_item_it_held()
    {
        _bank.Answer("cursor-one");

        // A link that got as far as writing its item down and no further, which is what a
        // request killed between the exchange and the save leaves behind.
        var user = await SomebodyWhoExists();
        var item = FakeBankConnector.Item("item-held", "token-held");

        Guid pendingId;

        using (var scope = _host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

            var pending = new PendingBankLink
            {
                UserId = user,
                Provider = FakeBankConnector.Name,
                ItemCiphertext = protector.Protect(JsonSerializer.Serialize(item, ItemJson)),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };

            dbContext.Add(pending);
            await dbContext.SaveChangesAsync(Ct);

            pendingId = pending.Id;
        }

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                .CompletePending(pendingId, Ct);
        }

        using (var scope = _host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var connection = Assert.Single(await dbContext.Set<BankConnection>().ToListAsync(Ct));

            Assert.Equal("item-held", connection.ProviderItemId);
            Assert.Equal(user, connection.UserId);

            // Gone, so a later sweep does not do this again.
            Assert.Empty(await dbContext.Set<PendingBankLink>().ToListAsync(Ct));
        }

        // And no second item was asked for: finishing costs nothing at the provider.
        Assert.Empty(_bank.RemovedTokens);
    }

    /// <summary>
    /// The sweep dispatches on what it read a moment ago, so by the time one of these runs
    /// the row may have been finished by the request that started it.
    /// </summary>
    [Fact]
    public async Task Finishing_a_link_that_is_no_longer_there_does_nothing()
    {
        using var scope = _host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
            .CompletePending(Guid.NewGuid(), Ct);

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Set<BankConnection>().ToListAsync(Ct));
    }

    /// <summary>
    /// An item that already belongs to somebody else's connection is refused, and the row
    /// that was holding it goes with the refusal.
    /// </summary>
    /// <remarks>
    /// A refusal is not an interruption. Held rows exist so that a link which could not be
    /// stored *yet* can be finished later, and this one can never be stored at all -- the
    /// sweep would try it to exhaustion and reach the same answer every time. Left there it
    /// would be a row belonging to one person holding a live access token to another
    /// person's bank, which is the last thing that should outlive a 404.
    /// </remarks>
    [Fact]
    public async Task An_item_that_is_somebody_elses_leaves_no_row_holding_their_access()
    {
        _bank.Answer("cursor-one");

        var item = FakeBankConnector.Item("item-shared", "token-shared");

        // Somebody else links first, so the item is theirs.
        _bank.AnswerExchange(item);

        using var somebodyElse = _host.ClientForAnotherUser();

        var theirs = await somebodyElse.PostAsJsonAsync(
            "/bank-connections", new CreateBankConnectionRequest { PublicToken = "public-token" }, Ct);

        theirs.EnsureSuccessStatusCode();

        // And now the provider hands the caller the same item.
        _bank.AnswerExchange(item);

        var mine = await _host.Client.PostAsJsonAsync(
            "/bank-connections", new CreateBankConnectionRequest { PublicToken = "public-token" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);

        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Set<PendingBankLink>().ToListAsync(Ct));

        // Still exactly the one connection, and its access was not handed back: the item is
        // theirs and in use, and removing it would break their connection rather than ours.
        Assert.Single(await dbContext.Set<BankConnection>().ToListAsync(Ct));
        Assert.Empty(_bank.RemovedTokens);
    }

    /// <summary>
    /// A link that has been tried enough times is given up on, and giving up means handing
    /// the access back rather than keeping it.
    /// </summary>
    /// <remarks>
    /// The row holds a working bank access token. Nothing surfaces it, nothing revisits it,
    /// and the item it names belongs to no connection anybody can see -- so leaving it is
    /// keeping somebody's bank access alive forever for no one's benefit. Handing it back is
    /// the one action that resolves both halves at once.
    /// </remarks>
    [Fact]
    public async Task Giving_up_hands_the_access_back_and_drops_the_row()
    {
        var pendingId = await Held(FakeBankConnector.Item("item-held", "token-held"));

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                .AbandonPending(pendingId, Ct);
        }

        Assert.Equal(["token-held"], _bank.RemovedTokens);

        await using var after = _host.Services.CreateAsyncScope();
        var dbContext = after.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Set<PendingBankLink>().ToListAsync(Ct));
        Assert.Empty(await dbContext.Set<BankConnection>().ToListAsync(Ct));
    }

    /// <summary>
    /// The provider refusing does not buy the row a reprieve: it goes either way, and the
    /// log is what is left naming the item somebody has to remove by hand.
    /// </summary>
    [Fact]
    public async Task Giving_up_drops_the_row_even_when_the_access_cannot_be_handed_back()
    {
        _bank.RefuseRemoval = true;

        var pendingId = await Held(FakeBankConnector.Item("item-stuck", "token-stuck"));

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBankConnectionService>()
                .AbandonPending(pendingId, Ct);
        }

        await using var after = _host.Services.CreateAsyncScope();

        Assert.Empty(await after.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<PendingBankLink>().ToListAsync(Ct));

        // Named, and at Error, because once the row is gone this line is the only record
        // that the item is still there.
        Assert.Contains(_host.Logs, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("item-stuck"));
    }

    /// <summary>A link written down as holding <paramref name="item"/> and nothing more.</summary>
    private async Task<Guid> Held(LinkedItem item)
    {
        var user = await SomebodyWhoExists();

        await using var scope = _host.Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

        var pending = new PendingBankLink
        {
            UserId = user,
            Provider = FakeBankConnector.Name,
            ItemCiphertext = protector.Protect(JsonSerializer.Serialize(item, ItemJson)),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };

        dbContext.Add(pending);
        await dbContext.SaveChangesAsync(Ct);

        return pending.Id;
    }

    /// <summary>The signed-in caller, as a row, so a pending link can be given an owner.</summary>
    private async Task<Guid> SomebodyWhoExists()
    {
        // Any authenticated call provisions the caller from their claims.
        await _host.Client.GetAsync("/bank-connections", Ct);

        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return (await dbContext.Set<GroupSplit.Data.Entities.User>().FirstAsync(Ct)).Id;
    }
}
