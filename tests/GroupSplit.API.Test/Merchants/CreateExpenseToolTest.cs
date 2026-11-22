using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Merchants;

/// <summary>
/// The MCP <c>create_expense</c> tool filling in a merchant's usual group and category
/// (#548), against a stand-in API.
/// </summary>
public class CreateExpenseToolTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_merchant_named_alone_brings_its_last_group_and_category()
    {
        var api = new StandInApi(History());

        await Create(api, new CreateTransactionRequest { Name = "Shop", Amount = 20, MerchantId = Merchant });

        Assert.Equal(Home, api.Created!.GroupId);
        Assert.Equal(Groceries, api.Created.CategoryId);
    }

    [Fact]
    public async Task A_category_already_given_is_kept_and_nothing_is_asked()
    {
        var api = new StandInApi(History());
        var mine = Guid.NewGuid();

        await Create(api, new CreateTransactionRequest
        {
            Name = "Shop",
            Amount = 20,
            MerchantId = Merchant,
            GroupId = Home,
            CategoryId = mine
        });

        Assert.Equal(mine, api.Created!.CategoryId);
        Assert.False(api.AskedForDefaults);
    }

    /// <summary>A category from Home means nothing in another group.</summary>
    [Fact]
    public async Task Another_group_given_takes_nothing_from_the_history()
    {
        var api = new StandInApi(History());
        var trip = Guid.NewGuid();

        await Create(api, new CreateTransactionRequest { Name = "Shop", Amount = 20, MerchantId = Merchant, GroupId = trip });

        Assert.Equal(trip, api.Created!.GroupId);
        Assert.Null(api.Created.CategoryId);
    }

    /// <summary>The classifier's guess is offered in the app and never filled in for an agent.</summary>
    [Fact]
    public async Task A_guess_without_history_fills_nothing()
    {
        var api = new StandInApi(new MerchantDefaultsResponse(
            new FilingDestinationSuggestion(Home, "Home", false, Groceries, "Groceries", 0.9)));

        await Create(api, new CreateTransactionRequest { Name = "Shop", Amount = 20, MerchantId = Merchant });

        Assert.Null(api.Created!.GroupId);
        Assert.Null(api.Created.CategoryId);
    }

    private static MerchantDefaultsResponse History() => new(
        new FilingDestinationSuggestion(Home, "Home", false, Groceries, "Groceries", 1),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);

    private static async Task Create(StandInApi api, CreateTransactionRequest expense)
    {
        using var http = new HttpClient(api) { BaseAddress = new Uri("https://api.example/") };
        await TransactionTools.CreateExpense(new TransactionsClient(http), new MerchantsClient(http), expense, Ct);
    }

    private sealed class StandInApi(MerchantDefaultsResponse defaults) : HttpMessageHandler
    {
        public CreateTransactionRequest? Created { get; private set; }

        public bool AskedForDefaults { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == $"/merchants/{Merchant}/defaults")
            {
                AskedForDefaults = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(defaults, options: Json) };
            }

            if (request.Method == HttpMethod.Post && path == "/transactions")
            {
                Created = await request.Content!.ReadFromJsonAsync<CreateTransactionRequest>(Json, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new TransactionResponse { Id = Guid.NewGuid(), Name = Created!.Name }, options: Json)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
