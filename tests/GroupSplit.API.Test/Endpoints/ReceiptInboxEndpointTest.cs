using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
namespace GroupSplit.API.Test.Endpoints;

public class ReceiptInboxEndpointTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task Suggestions_are_batched_only_for_owned_waiting_receipts()
    {
        IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)>? asked = null;
        var suggester = new Mock<IFilingSuggester>();
        suggester.Setup(s => s.Suggest(It.IsAny<IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)>, CancellationToken>((rows, _) => asked = rows)
            .ReturnsAsync((IReadOnlyList<(FilingInput Row, FilingReceipt? Receipt)> rows, CancellationToken _) =>
                rows.Select(row => new FilingSuggestionResult(row.Row.Id, new FilingDestinationSuggestion(Guid.NewGuid(), "Home", false, Guid.NewGuid(), "Food", .9))).ToArray());
        await using var host = await ApiEndpointHost.StartAsync(services => services.AddSingleton(suggester.Object));
        var own = Guid.NewGuid();
        var ignored = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var draft = new SaveReceiptInboxRequest { Receipt = new SaveReceiptRequest { MerchantName = "Store", Total = 1, Subtotal = 1, Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 1, TotalPrice = 1 }] } };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{own}", draft, Json)).EnsureSuccessStatusCode();
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{ignored}", draft, Json)).EnsureSuccessStatusCode();
        (await host.Client.PostAsync($"/receipts/inbox/{ignored}/ignore", null)).EnsureSuccessStatusCode();
        using var other = host.ClientForAnotherUser();
        (await other.PutAsJsonAsync($"/receipts/inbox/{foreign}", draft, Json)).EnsureSuccessStatusCode();
        var response = await host.Client.PostAsJsonAsync("/receipts/inbox/suggestions", new ReceiptInboxSuggestionsRequest { ReceiptIds = [own, ignored, foreign] }, Json);
        response.EnsureSuccessStatusCode();
        Assert.Equal(own, Assert.Single(asked!).Row.Id);
        Assert.Equal(own, Assert.Single((await response.Content.ReadFromJsonAsync<List<ReceiptInboxSuggestion>>(Json))!).ReceiptId);
        Assert.False((await host.Client.PostAsJsonAsync("/receipts/inbox/suggestions", new ReceiptInboxSuggestionsRequest { ReceiptIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() }, Json)).IsSuccessStatusCode);
    }
    [Fact]
    public async Task Receipt_status_filters_preserve_privacy_and_restore_ignored_receipts()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var receipt = new SaveReceiptRequest { MerchantName = "Store", Subtotal = 10, Total = 10,
            Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 10, TotalPrice = 10 }] };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Receipt = receipt }, Json)).EnsureSuccessStatusCode();
        using var other = host.ClientForAnotherUser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/receipts/inbox/{id}/ignore", null)).StatusCode);
        (await host.Client.PostAsync($"/receipts/inbox/{id}/ignore", null)).EnsureSuccessStatusCode();
        Assert.Empty((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!);
        Assert.Equal(id, Assert.Single((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox/ignored", Json))!).Id);
        Assert.Empty((await other.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox/ignored", Json))!);
        var file = new FileReceiptInboxRequest { NewExpense = new CreateTransactionRequest { Name = "Purchase", Amount = 10, Currency = "USD", DateTime = DateTimeOffset.UtcNow } };
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", file, Json)).StatusCode);
        (await host.Client.PostAsync($"/receipts/inbox/{id}/restore", null)).EnsureSuccessStatusCode();
        Assert.Equal(id, Assert.Single((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox", Json))!).Id);
        (await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", file, Json)).EnsureSuccessStatusCode();
        Assert.Equal(id, Assert.Single((await host.Client.GetFromJsonAsync<List<ReceiptInboxResponse>>("/receipts/inbox/added", Json))!).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/receipts/inbox/{id}/ignore", null)).StatusCode);
    }
    [Fact]
    public async Task Filing_a_receipt_preserves_reviewed_tax_allocation()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var receipt = new SaveReceiptRequest { MerchantName = "Store", Subtotal = 20m, Tax = 2m, Total = 22m,
            Items = [new ReceiptItemInput { Name = "One", UnitPrice = 10m, TotalPrice = 10m },
                new ReceiptItemInput { Name = "Two", UnitPrice = 10m, TotalPrice = 10m }] };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Receipt = receipt }, Json)).EnsureSuccessStatusCode();
        var reviewed = receipt with { Items = [receipt.Items[0] with { TaxAmount = 1.25m }, receipt.Items[1] with { TaxAmount = .75m }] };
        var expense = new CreateTransactionRequest { Name = "Purchase", Amount = 22m, Currency = "USD",
            DateTime = DateTimeOffset.UtcNow, Receipt = reviewed };
        expense.Receipt = reviewed with { Items = [reviewed.Items[0] with { TaxAmount = 1.26m }, reviewed.Items[1]] };
        var invalid = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest { NewExpense = expense }, Json);
        await AssertProblem(invalid, HttpStatusCode.UnprocessableContent, "RECEIPT_DOES_NOT_ADD_UP");
        expense.Receipt = reviewed;
        var response = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest { NewExpense = expense }, Json);
        response.EnsureSuccessStatusCode();
        var filed = await response.Content.ReadFromJsonAsync<ReceiptInboxResponse>(Json);
        Assert.Equal(new[] { 1.25m, .75m }, filed!.Receipt.Items.Select(item => item.TaxAmount));
        var stored = await host.Client.GetFromJsonAsync<ReceiptResponse>($"/transactions/{filed.ExpenseId}/receipt", Json);
        Assert.Equal(2m, stored!.Items.Sum(item => item.TaxAmount));
    }
    [Fact]
    public async Task Import_distributes_unassigned_tax_but_preserves_exempt_and_known_lines()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var receipt = new SaveReceiptRequest { Subtotal = 30m, Tax = 2m, Total = 32m,
            Items = [new ReceiptItemInput { Name = "Known", UnitPrice = 10m, TotalPrice = 10m, TaxAmount = .50m },
                new ReceiptItemInput { Name = "Unknown", UnitPrice = 10m, TotalPrice = 10m },
                new ReceiptItemInput { Name = "Exempt", UnitPrice = 10m, TotalPrice = 10m, IsTaxable = false }] };
        var response = await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Receipt = receipt }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ReceiptInboxResponse>(Json);
        Assert.Equal(new[] { .50m, 1.50m, 0m }, result!.Receipt.Items.Select(item => item.TaxAmount));
    }
    [Fact]
    public async Task Extracted_details_do_not_replace_existing_receipt_details()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var original = new SaveReceiptRequest { MerchantName = "Original", Total = 10, Subtotal = 10,
            Items = [new ReceiptItemInput { Name = "Original item", UnitPrice = 10, TotalPrice = 10 }] };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Currency = "CAD", Receipt = original }, Json)).EnsureSuccessStatusCode();
        var replacement = new SaveReceiptRequest { MerchantName = "Replacement", Total = 5, Subtotal = 5,
            Items = [new ReceiptItemInput { Name = "Replacement item", UnitPrice = 5, TotalPrice = 5 }] };
        var refused = await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}/extracted", replacement, Json);
        await AssertProblem(refused, HttpStatusCode.Conflict, "RECEIPT_INVALID");
        var actual = await host.Client.GetFromJsonAsync<ReceiptInboxResponse>($"/receipts/inbox/{id}", Json);
        Assert.Equal("Original", actual!.Receipt.MerchantName);
        Assert.Equal("CAD", actual.Currency);
        Assert.Equal("Original item", Assert.Single(actual.Receipt.Items).Name);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"/receipts/inbox/{Guid.NewGuid()}/extracted", replacement, Json)).StatusCode);
    }
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
    [Fact]
    public async Task Creating_expense_with_a_receipt_keeps_the_original_draft_identity()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var id = Guid.NewGuid();
        var bill = new SaveReceiptRequest { MerchantName = "Store", Subtotal = 10, Total = 10,
            Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 10, TotalPrice = 10 }] };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest { Receipt = bill }, Json)).EnsureSuccessStatusCode();
        var response = await host.Client.PostAsJsonAsync($"/receipts/inbox/{id}/file", new FileReceiptInboxRequest
        { NewExpense = new CreateTransactionRequest { Name = "Store", Amount = 10, DateTime = DateTimeOffset.UtcNow, Receipt = bill } }, Json);
        response.EnsureSuccessStatusCode();
        var filed = await response.Content.ReadFromJsonAsync<ReceiptInboxResponse>(Json);
        Assert.NotNull(filed?.ExpenseId);
        var saved = await host.Client.GetFromJsonAsync<ReceiptResponse>($"/transactions/{filed!.ExpenseId}/receipt", Json);
        Assert.Equal(id, saved!.Id);
        Assert.Equal("Item", Assert.Single(saved.Items).Name);
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
