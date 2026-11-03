using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
namespace GroupSplit.API.Test.Endpoints;

public class ReceiptInboxEndpointTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task Draft_is_private_persistent_and_deletable()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var saved = await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest
        { Currency = "CAD", Receipt = new() { MerchantName = "Costco", Total = 20, Subtotal = 20,
            Items = [new() { Name = "Lunch", TotalPrice = 20, UnitPrice = 20 }] } }, Json);
        saved.EnsureSuccessStatusCode();
        var rows = await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json);
        Assert.Equal("CAD", Assert.Single(rows!).Currency);
        using var other = host.ClientForAnotherUser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/receipts/inbox/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/receipts/inbox/{id}")).StatusCode);
        (await host.Client.DeleteAsync($"/receipts/inbox/{id}")).EnsureSuccessStatusCode();
        Assert.Empty((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!);
    }
    [Fact]
    public async Task Filing_is_retryable_and_preserves_existing_expense()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest
        { Receipt = new() { MerchantName = "Store", Total = 20, Subtotal = 20,
            Items = [new() { Name = "Food", TotalPrice = 20, UnitPrice = 20 }] } }, Json)).EnsureSuccessStatusCode();
        var expense = await host.Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        { Name = "Food", Amount = 20, Currency = "USD", DateTime = DateTimeOffset.UtcNow }, Json);
        expense.EnsureSuccessStatusCode();
        var expenseId = (await expense.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var before = await host.Client.GetStringAsync($"/transactions/{expenseId}");
        var request = new FileReceiptInboxRequest { ExpenseId = expenseId };
        (await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", request, Json)).EnsureSuccessStatusCode();
        (await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", request, Json)).EnsureSuccessStatusCode();
        Assert.Empty((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!);
        var bill = await host.Client.GetFromJsonAsync<ReceiptResponse>($"/transactions/{expenseId}/receipt", Json);
        Assert.Equal(id, bill!.Id);
        Assert.Equal("Food", Assert.Single(bill.Items).Name);
    }
    [Fact]
    public async Task Creating_from_a_draft_is_retryable_and_refuses_currency_mismatch_atomically()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Currency = "CAD", Receipt = new() { MerchantName = "Store", Subtotal = 10, Total = 10, Items = [new() { Name = "Item", UnitPrice = 10, TotalPrice = 10 }] } }, Json)).EnsureSuccessStatusCode();
        var input = new CreateTransactionRequest { Name = "Receipt purchase", Amount = 10, Currency = "USD", DateTime = DateTimeOffset.UtcNow };
        var refused = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest { NewExpense = input }, Json);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Single((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!);
        input.Currency = "CAD";
        var request = new FileReceiptInboxRequest { NewExpense = input };
        var first = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", request, Json);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<ReceiptInboxResponse>(Json);
        var retry = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", request, Json);
        retry.EnsureSuccessStatusCode();
        Assert.Equal(firstBody!.ExpenseId, (await retry.Content.ReadFromJsonAsync<ReceiptInboxResponse>(Json))!.ExpenseId);
    }    [Fact]
    public async Task Inbox_errors_use_the_standard_problem_contract()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var missing = await host.Client.GetAsync($"/receipts/inbox/{id}");
        await AssertProblem(missing, HttpStatusCode.NotFound, "RECEIPT_NOT_FOUND");
        var invalid = await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new { currency = (string?)null }, Json);
        await AssertProblem(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Receipt = new() { MerchantName = "Store", Subtotal = 10, Total = 10, Items = [new() { Name = "Item", UnitPrice = 10, TotalPrice = 10 }] } }, Json)).EnsureSuccessStatusCode();
        var destination = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest(), Json);
        await AssertProblem(destination, HttpStatusCode.BadRequest, "RECEIPT_INVALID");
        var mismatch = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest
        { NewExpense = new() { Name = "Purchase", Currency = "CAD", Amount = 10, DateTime = DateTimeOffset.UtcNow } }, Json);
        await mismatch.Content.LoadIntoBufferAsync();
        await AssertProblem(mismatch, HttpStatusCode.Conflict, "CURRENCY_MISMATCH");
        var body = JsonSerializer.Deserialize<JsonElement>(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("USD", body.GetProperty("receiptCurrency").GetString());
        Assert.Equal("CAD", body.GetProperty("expenseCurrency").GetString());
    }

    [Fact]
    public async Task Empty_drafts_are_refused_without_creating_an_inbox_entry()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        foreach (var input in new[] { new SaveReceiptInboxRequest(), new SaveReceiptInboxRequest { Receipt = null } })
        {
            var response = await host.Client.PutAsJsonAsync($"/receipts/inbox/{Guid.NewGuid()}", input, Json);
            await AssertProblem(response, HttpStatusCode.BadRequest, "RECEIPT_INVALID");
        }
        Assert.Empty((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!);
    }
    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal("https://groupsplit.app/errors/" + code.ToLowerInvariant().Replace('_', '-'), body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("instance").GetString()));
    }}
