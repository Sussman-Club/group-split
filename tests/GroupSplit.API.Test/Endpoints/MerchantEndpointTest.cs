using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// The merchant routes over the wire, as two accounts sharing no group. A merchant is a
/// row every account can end up pointing at, so these are the tests that say one account
/// cannot change what another sees there, nor learn which places the other has been.
/// </summary>
public class MerchantEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiEndpointHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>
    /// A place the host's own user has added and recorded a personal expense at, which is
    /// the ordinary way a merchant becomes somebody's.
    /// </summary>
    private async Task<MerchantResponse> SpentAt(HttpClient client, string name, string? logo = null)
    {
        var created = await client.PostAsJsonAsync("/merchants",
            new CreateMerchantRequest { Name = name, LogoUrl = logo }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var merchant = (await created.Content.ReadFromJsonAsync<MerchantResponse>(Json, Ct))!;

        var expense = await client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Appointment",
            Amount = 80m,
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        }, Json, Ct);
        expense.EnsureSuccessStatusCode();

        return merchant;
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("code").GetString();

    [Fact]
    public async Task A_place_only_another_account_has_been_is_not_in_my_listing()
    {
        await SpentAt(_host.Client, "Dr. Ruiz Psychiatry");
        var stranger = _host.ClientForAnotherUser();

        var everything = await stranger.GetFromJsonAsync<List<MerchantResponse>>("/merchants", Json, Ct);
        var searched = await stranger.GetFromJsonAsync<List<MerchantResponse>>("/merchants?search=ruiz", Json, Ct);

        Assert.Empty(everything!);
        Assert.Empty(searched!);
    }

    [Fact]
    public async Task A_place_only_another_account_has_been_cannot_be_read_by_id_either()
    {
        var merchant = await SpentAt(_host.Client, "Dr. Ruiz Psychiatry");

        var response = await _host.ClientForAnotherUser().GetAsync($"/merchants/{merchant.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantNotFound, await Code(response));
    }

    [Fact]
    public async Task My_own_place_is_in_my_listing_with_my_own_count()
    {
        var merchant = await SpentAt(_host.Client, "Lidl");

        var listed = await _host.Client.GetFromJsonAsync<List<MerchantResponse>>("/merchants?search=li", Json, Ct);

        var only = Assert.Single(listed!);
        Assert.Equal(merchant.Id, only.Id);
        Assert.Equal(1, only.TransactionCount);
    }

    [Fact]
    public async Task Another_account_cannot_rename_or_relogo_a_place()
    {
        var merchant = await SpentAt(_host.Client, "Lidl",
            "https://plaid-merchant-logos.plaid.com/lidl_1.png");

        var response = await _host.ClientForAnotherUser().PutAsJsonAsync($"/merchants/{merchant.Id}",
            new UpdateMerchantRequest
            {
                Name = "Somewhere rude",
                LogoUrl = "https://plaid-merchant-logos.plaid.com/other_2.png"
            }, Json, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantEditForbidden, await Code(response));

        var after = await _host.Client.GetFromJsonAsync<MerchantResponse>($"/merchants/{merchant.Id}", Json, Ct);
        Assert.Equal("Lidl", after!.Name);
        Assert.Equal("https://plaid-merchant-logos.plaid.com/lidl_1.png", after.LogoUrl);
    }

    [Fact]
    public async Task Another_account_cannot_delete_a_place()
    {
        var merchant = await SpentAt(_host.Client, "Lidl");

        var response = await _host.ClientForAnotherUser().DeleteAsync($"/merchants/{merchant.Id}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantEditForbidden, await Code(response));
        Assert.Equal(HttpStatusCode.OK, (await _host.Client.GetAsync($"/merchants/{merchant.Id}", Ct)).StatusCode);
    }

    /// <summary>
    /// Not only strangers: the place is shared with everyone who has spent there, so being
    /// one of them does not make it yours to change either.
    /// </summary>
    [Fact]
    public async Task Nobody_can_rename_a_place_not_even_somebody_who_has_spent_there()
    {
        var merchant = await SpentAt(_host.Client, "Lidl");

        var response = await _host.Client.PutAsJsonAsync($"/merchants/{merchant.Id}",
            new UpdateMerchantRequest { Name = "Lidl GmbH" }, Json, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantEditForbidden, await Code(response));
    }

    [Theory]
    [InlineData("https://tracker.example.com/pixel.png")]
    [InlineData("http://plaid-merchant-logos.plaid.com/lidl.png")]
    [InlineData("https://plaid-merchant-logos.plaid.com.example.com/lidl.png")]
    [InlineData("https://plaid-merchant-logos.plaid.com:8443/lidl.png")]
    [InlineData("https://someone@plaid-merchant-logos.plaid.com/lidl.png")]
    [InlineData("/_content/GroupSplit.App.Shared/merchants/../../../evil.svg")]
    [InlineData("//tracker.example.com/pixel.png")]
    [InlineData("javascript:alert(1)")]
    public async Task A_logo_from_anywhere_but_the_provider_is_refused(string logo)
    {
        var response = await _host.Client.PostAsJsonAsync("/merchants",
            new CreateMerchantRequest { Name = "Lidl", LogoUrl = logo }, Json, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantLogoNotAllowed, await Code(response));
    }

    [Fact]
    public async Task A_one_letter_search_is_refused()
    {
        var response = await _host.Client.GetAsync("/merchants?search=l", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.MerchantSearchTooShort, await Code(response));
    }

    /// <summary>
    /// Adding a place somebody else's bank already reported hands back that row. The caller
    /// could not have seen it to pick it, so refusing would leave them unable to name it;
    /// and the logo they asked for is not applied, since that would be a relogo by the
    /// back door.
    /// </summary>
    [Fact]
    public async Task Adding_a_place_that_exists_hands_back_the_one_there_is_without_relogoing_it()
    {
        var merchant = await SpentAt(_host.Client, "Lidl",
            "https://plaid-merchant-logos.plaid.com/lidl_1.png");

        var response = await _host.ClientForAnotherUser().PostAsJsonAsync("/merchants",
            new CreateMerchantRequest
            {
                Name = "LIDL",
                LogoUrl = "https://plaid-merchant-logos.plaid.com/other_2.png"
            }, Json, Ct);

        response.EnsureSuccessStatusCode();
        var found = (await response.Content.ReadFromJsonAsync<MerchantResponse>(Json, Ct))!;

        Assert.Equal(merchant.Id, found.Id);
        Assert.Equal("https://plaid-merchant-logos.plaid.com/lidl_1.png", found.LogoUrl);

        // The other account's spending there is theirs: the count is of the caller's own.
        Assert.Equal(0, found.TransactionCount);
    }
}
