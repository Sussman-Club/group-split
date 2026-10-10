using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The clients' <see cref="PatchDocument{TModel}"/> against the JSON Patch library the API
/// reads it with.
/// </summary>
/// <remarks>
/// The web client and the CLI write their patches without
/// <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c>, which neither of them can take as
/// a dependency, so nothing in their own builds says that what they send is still what the
/// API accepts. This does, twice over: the bytes have to be the ones the library writes for
/// the same calls, and a body written that way has to land on the expense, sent over the
/// API's real pipeline the way the generated clients send it.
/// </remarks>
public class ClientPatchWireFormatTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>The body as the generated clients write it: their settings, their media type.</summary>
    private static StringContent Body<T>(PatchDocument<T> patch) where T : class =>
        new(JsonSerializer.Serialize(patch, GroupSplitSerializer.Options),
            Encoding.UTF8,
            "application/json-patch+json");

    private static readonly Guid Someone = Guid.Parse("6f1d2c4e-8a3b-4f6d-9c2e-1b7a5d3e9f01");
    private static readonly DateTimeOffset Friday = new(2026, 10, 2, 21, 30, 0, TimeSpan.FromHours(-4));

    /// <summary>
    /// Every kind of value a client patch carries: text, a null, money, a moment with its
    /// offset, an identifier, and a list of shares.
    /// </summary>
    [Fact]
    public void The_clients_write_the_bytes_the_library_writes()
    {
        var splits = new List<SplitInput> { new() { UserId = Someone, Amount = 120m } };

        var ours = new PatchDocument<UpdateTransactionRequest>()
            .Replace(x => x.Name, "Late dinner")
            .Replace(x => x.Description, null)
            .Replace(x => x.Amount, 120.50m)
            .Replace(x => x.DateTime, Friday)
            .Replace(x => x.CategoryId, Someone)
            .Replace(x => x.MerchantId, null)
            .Replace(x => x.Splits, splits);

        var theirs = new JsonPatchDocument<UpdateTransactionRequest>()
            .Replace(x => x.Name, "Late dinner")
            .Replace(x => x.Description, null)
            .Replace(x => x.Amount, 120.50m)
            .Replace(x => x.DateTime, Friday)
            .Replace(x => x.CategoryId, Someone)
            .Replace(x => x.MerchantId, null)
            .Replace(x => x.Splits, splits);

        Assert.Equal(
            JsonSerializer.Serialize(theirs, GroupSplitSerializer.Options),
            JsonSerializer.Serialize(ours, GroupSplitSerializer.Options));
    }

    [Fact]
    public void A_group_patch_is_written_as_the_library_writes_it()
    {
        var ours = new PatchDocument<CreateGroupRequest>()
            .Replace(x => x.Name, "Trip to Lisbon")
            .Replace(x => x.DefaultSplitRuleId, null);

        var theirs = new JsonPatchDocument<CreateGroupRequest>()
            .Replace(x => x.Name, "Trip to Lisbon")
            .Replace(x => x.DefaultSplitRuleId, null);

        Assert.Equal(
            JsonSerializer.Serialize(theirs, GroupSplitSerializer.Options),
            JsonSerializer.Serialize(ours, GroupSplitSerializer.Options));
    }

    [Fact]
    public async Task An_expense_patched_by_a_client_reads_back_as_the_patch_said()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!.Id;
        var group = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = "Trip" }, Json, Ct);
        group.EnsureSuccessStatusCode();
        var groupId = (await group.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Hotel",
            Description = "One night",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me
        }, Json, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!.Id;

        var patch = new PatchDocument<UpdateTransactionRequest>()
            .Replace(x => x.Name, "Hotel, two nights")
            .Replace(x => x.Description, null)
            .Replace(x => x.Amount, 240.50m)
            .Replace(x => x.DateTime, Friday)
            .Replace(x => x.Splits, [new SplitInput { UserId = me, Amount = 240.50m }]);

        var response = await Client.PatchAsync($"/transactions/{id}", Body(patch), Ct);
        response.EnsureSuccessStatusCode();

        var stored = (await Client.GetFromJsonAsync<TransactionDetailsResponse>($"/transactions/{id}", Json, Ct))!;
        Assert.Equal("Hotel, two nights", stored.Name);
        Assert.Null(stored.Description);
        Assert.Equal(240.50m, stored.Amount);
        Assert.Equal(Friday, stored.DateTime);
        var share = Assert.Single(stored.Splits);
        Assert.Equal(me, share.UserId);
        Assert.Equal(240.50m, share.Amount);
    }

    [Fact]
    public async Task A_group_renamed_by_a_client_reads_back_with_the_new_name()
    {
        var group = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = "Trip" }, Json, Ct);
        group.EnsureSuccessStatusCode();
        var id = (await group.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var patch = new PatchDocument<CreateGroupRequest>().Replace(x => x.Name, "Trip to Lisbon");

        var response = await Client.PatchAsync($"/groups/{id}", Body(patch), Ct);
        response.EnsureSuccessStatusCode();

        var stored = (await Client.GetFromJsonAsync<GroupResponse>($"/groups/{id}", Json, Ct))!;
        Assert.Equal("Trip to Lisbon", stored.Name);
    }
}
