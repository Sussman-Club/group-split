using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// How a transaction came to be recorded, as the endpoints write and read it (#549): told
/// by the token a request carries, not by anything the caller says about itself.
/// </summary>
public class EnteredByEndpointTest
{
    private const string McpClient = "groupsplit-mcp";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task What_comes_through_MCP_says_so_and_group_activity_narrows_to_it()
    {
        // The production factory, given the exchange client the AppHost would give it.
        await using var host = await ApiEndpointHost.StartAsync(services =>
            services.AddScoped<IEntryChannel>(provider =>
                new EntryChannel(provider.GetRequiredService<IHttpContextAccessor>(), McpClient)));

        var group = await Read<GroupResponse>(await host.Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Home" }, Json, Ct));

        using var mcp = host.ClientIssuedTo(McpClient);
        using var web = host.ClientIssuedTo("groupsplit-web");

        var typed = await Created(host.Client, group.Id, "Typed");
        var assisted = await Created(mcp, group.Id, "Assisted");
        var fromAnotherClient = await Created(web, group.Id, "From the web");

        Assert.Equal(TransactionSource.Manual, typed.Source);
        Assert.Equal(TransactionSource.Mcp, assisted.Source);
        Assert.Equal(TransactionSource.Manual, fromAnotherClient.Source);
        Assert.Equal(typed.PaidByUserId, assisted.EnteredByUserId);

        // On the wire by name, in both directions: the query string asks for "Mcp" and the
        // rows answer with it, as the MCP tool's callers read them.
        var page = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/groups/{group.Id}/activity?source=Mcp", Json, Ct);
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("Assisted", row.GetProperty("name").GetString());
        Assert.Equal("Mcp", row.GetProperty("source").GetString());
        Assert.Equal(typed.PaidByUserId, row.GetProperty("enteredByUserId").GetGuid());

        var details = await host.Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{assisted.Id}", Json, Ct);
        Assert.Equal(TransactionSource.Mcp, details!.Source);
    }

    [Fact]
    public async Task A_receipt_filed_from_the_inbox_says_it_came_from_a_receipt()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var receipt = new SaveReceiptRequest
        {
            MerchantName = "Store",
            Subtotal = 10,
            Total = 10,
            Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 10, TotalPrice = 10 }]
        };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}",
            new SaveReceiptInboxRequest { Receipt = receipt }, Json, Ct)).EnsureSuccessStatusCode();

        var filed = await Read<ReceiptInboxResponse>(await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file",
            new FileReceiptInboxRequest
            {
                NewExpense = new CreateTransactionRequest
                {
                    Name = "Purchase",
                    Amount = 10,
                    Currency = "USD",
                    DateTime = DateTimeOffset.UtcNow
                }
            }, Json, Ct));

        var details = await host.Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{filed.ExpenseId}", Json, Ct);
        Assert.Equal(TransactionSource.Receipt, details!.Source);
    }

    private static async Task<TransactionResponse> Created(HttpClient client, Guid group, string name) =>
        await Read<TransactionResponse>(await client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            GroupId = group,
            Name = name,
            Amount = 10,
            Currency = "USD",
            DateTime = DateTimeOffset.UtcNow
        }, Json, Ct));

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
