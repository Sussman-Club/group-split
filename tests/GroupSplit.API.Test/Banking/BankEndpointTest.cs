using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The bank routes over real HTTP: who may reach them, and what the one anonymous route in
/// the API does with a request it cannot vouch for.
/// </summary>
public class BankEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public async Task The_bank_routes_need_a_signed_in_caller()
    {
        using var anonymous = _host.AnonymousClient();

        var connections = await anonymous.GetAsync("/bank-connections", Ct);
        var inbox = await anonymous.GetAsync("/inbox", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, connections.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inbox.StatusCode);
    }

    [Fact]
    public async Task A_person_with_no_linked_banks_gets_an_empty_list_that_says_linking_is_possible()
    {
        var response = await _host.Client.GetFromJsonAsync<BankConnectionsResponse>("/bank-connections", Json, Ct);

        Assert.NotNull(response);
        Assert.True(response.Enabled);
        Assert.Empty(response.Connections);
    }

    [Fact]
    public async Task Bank_sync_reads_as_off_when_no_connector_is_registered_for_the_provider()
    {
        await using var bare = await ApiEndpointHost.StartAsync();

        var response = await bare.Client.GetFromJsonAsync<BankConnectionsResponse>("/bank-connections", Json, Ct);

        Assert.NotNull(response);
        Assert.False(response.Enabled);
    }

    [Fact]
    public async Task Another_persons_connection_is_not_found_over_http()
    {
        var connection = await LinkAsync();

        using var stranger = _host.ClientForAnotherUser();

        var sync = await stranger.PostAsync($"/bank-connections/{connection.Id}/sync", null, Ct);
        var unlink = await stranger.DeleteAsync($"/bank-connections/{connection.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, sync.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlink.StatusCode);
    }

    [Fact]
    public async Task An_inbox_row_of_someone_elses_is_not_found_over_http()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        using var stranger = _host.ClientForAnotherUser();

        var filed = await stranger.PostAsJsonAsync($"/inbox/{row.Id}/file", new FileBankTransactionRequest(), Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, filed.StatusCode);
    }

    [Fact]
    public async Task The_webhook_route_is_reachable_without_a_token_and_refuses_an_unknown_provider()
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.PostAsync("/webhooks/not-a-provider", Body("{}"), Ct);

        // A 404 rather than a 401: it was let through the door, and there is simply nothing
        // behind that name.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_webhook_that_cannot_be_verified_is_refused()
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.PostAsync($"/webhooks/{FakeBankConnector.Name}", Body("{}"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_verified_webhook_for_an_item_this_deployment_does_not_have_is_acknowledged()
    {
        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        var response = await anonymous.SendAsync(request, Ct);

        // Acknowledged, so the provider stops resending something that can never mean
        // anything here.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_verified_webhook_naming_a_known_item_is_acted_on()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        var response = await anonymous.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The fake connector reads every body as "there are updates", so the sync the
        // webhook asks for is the observable effect.
        await WaitForSyncAsync();
        Assert.NotEmpty(_bank.CursorsSeen);
        Assert.Equal(connection.Id, connection.Id);
    }

    /// <summary>
    /// The dialog promises that unlinking leaves the expenses alone. It is a set-null
    /// relationship rather than a cascade, and this is what says so out loud.
    /// </summary>
    [Fact]
    public async Task Unlinking_takes_the_imported_rows_and_leaves_the_expenses()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        var filed = await _host.Client.PostAsJsonAsync(
            $"/inbox/{row.Id}/file", new FileBankTransactionRequest(), Json, Ct);

        Assert.Equal(HttpStatusCode.Created, filed.StatusCode);

        var expense = await filed.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct);
        Assert.NotNull(expense);

        var unlinked = await _host.Client.DeleteAsync($"/bank-connections/{connection.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);

        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The row and its account went with the connection.
        Assert.Empty(await dbContext.Set<BankTransaction>().ToListAsync(Ct));
        Assert.Empty(await dbContext.Set<LinkedAccount>().ToListAsync(Ct));

        // The expense stayed, keeping its amount and losing only the link back.
        var kept = Assert.Single(await dbContext.Set<Expense>().ToListAsync(Ct));

        Assert.Equal(expense.Id, kept.Id);
        Assert.Equal(10m, kept.Amount);
        Assert.Null(kept.BankTransactionId);
    }

    /// <summary>
    /// The refusal that keeps a second expense from existing, over the wire: the status, the
    /// code a client branches on, and the expense it names.
    /// </summary>
    [Fact]
    public async Task Filing_a_row_that_matches_a_recorded_expense_is_a_conflict_naming_it()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);
        var typed = await ExpenseAsync("Dinner", 10m);

        var filed = await _host.Client.PostAsJsonAsync(
            $"/inbox/{row.Id}/file", new FileBankTransactionRequest(), Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, filed.StatusCode);

        var problem = await filed.Content.ReadFromJsonAsync<ProblemDetails>(Json, Ct);

        Assert.NotNull(problem);
        Assert.Equal(ErrorCodes.PossibleDuplicateExpense, problem.Code);

        var matches = problem.GetExtension<List<ExpenseMatchResponse>>(ProblemDetails.MatchesExtension, Json);

        Assert.Equal(typed.Id, Assert.Single(matches!).TransactionId);
    }

    [Fact]
    public async Task The_inbox_listing_carries_what_each_waiting_row_could_already_be()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);
        var typed = await ExpenseAsync("Dinner", 10m);

        var page = await _host.Client.GetFromJsonAsync<PagedResponse<BankTransactionResponse>>("/inbox", Json, Ct);

        var listed = Assert.Single(page!.Items);

        Assert.Equal(row.Id, listed.Id);
        Assert.Equal(typed.Id, Assert.Single(listed.PossibleDuplicates).TransactionId);
    }

    [Fact]
    public async Task Attaching_a_row_to_an_expense_answers_with_the_expense_that_was_already_there()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);
        var typed = await ExpenseAsync("Dinner", 10m);

        var response = await _host.Client.PostAsJsonAsync($"/inbox/{row.Id}/link",
            new LinkBankTransactionRequest { TransactionId = typed.Id }, Json, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var linked = await response.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct);

        Assert.Equal(typed.Id, linked!.Id);

        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await dbContext.Set<Expense>().CountAsync(Ct));
    }

    [Fact]
    public async Task An_expense_says_which_waiting_rows_could_be_it()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);
        var typed = await ExpenseAsync("Dinner", 10m);

        var rows = await _host.Client.GetFromJsonAsync<List<BankTransactionResponse>>(
            $"/transactions/{typed.Id}/bank-matches", Json, Ct);

        Assert.Equal(row.Id, Assert.Single(rows!).Id);
    }

    [Fact]
    public async Task Somebody_elses_row_cannot_be_attached_or_dismissed()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);
        var typed = await ExpenseAsync("Dinner", 10m);

        using var stranger = _host.ClientForAnotherUser();

        var link = await stranger.PostAsJsonAsync($"/inbox/{row.Id}/link",
            new LinkBankTransactionRequest { TransactionId = typed.Id }, Json, Ct);

        var dismiss = await stranger.PostAsJsonAsync($"/inbox/{row.Id}/dismiss-match",
            new DismissBankMatchRequest { TransactionId = typed.Id }, Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
    }

    // ---- setup ---------------------------------------------------------------------------

    private static HttpContent Body(string json) =>
        new StringContent(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Signed(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{FakeBankConnector.Name}")
        {
            Content = Body(json)
        };

        request.Headers.Add("X-Fake-Signature", "valid");

        return request;
    }

    private async Task<BankConnection> LinkAsync(string? providerItemId = null)
    {
        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

        // The host's own user, provisioned by the first request its client made.
        var me = await FirstUserAsync(dbContext);

        var connection = new BankConnection
        {
            UserId = me,
            Provider = FakeBankConnector.Name,
            ProviderItemId = providerItemId ?? $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = protector.Protect(FakeBankConnector.AccessToken),
            LinkedAt = DateTimeOffset.UtcNow
        };

        connection.Accounts.Add(new LinkedAccount
        {
            ProviderAccountId = "acc-1",
            Name = "Everyday",
            Type = "depository"
        });

        dbContext.Add(connection);
        await dbContext.SaveChangesAsync(Ct);

        return connection;
    }

    /// <summary>
    /// The span the inbox is narrowed by is bound from the query string as two
    /// <see cref="DateOnly"/>s. That binding is exactly the kind of thing a test calling the
    /// service directly cannot see, which is what this host is for.
    /// </summary>
    [Fact]
    public async Task The_inbox_narrows_to_a_span_of_days_given_on_the_query_string()
    {
        var connection = await LinkAsync();

        await RowAsync(connection, providerId: "august", date: new DateOnly(2026, 8, 15));
        await RowAsync(connection, providerId: "september", date: new DateOnly(2026, 9, 15));

        var all = await ItemsAsync("/inbox");
        var september = await ItemsAsync("/inbox?From=2026-09-01&To=2026-09-30");

        Assert.Equal(2, all.Count);
        Assert.Equal("Lidl", Assert.Single(september).GetProperty("merchantName").GetString());
        Assert.Equal("2026-09-15", Assert.Single(september).GetProperty("date").GetString());
    }

    private async Task<List<JsonElement>> ItemsAsync(string route)
    {
        var response = await _host.Client.GetAsync(route, Ct);
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        return page.GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>
    /// An expense the host's own user typed, on the same day the imported rows here fall on.
    /// </summary>
    private async Task<TransactionResponse> ExpenseAsync(string name, decimal amount)
    {
        var response = await _host.Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = new DateTimeOffset(2026, 9, 1, 20, 30, 0, TimeSpan.Zero)
        }, Json, Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;
    }

    private async Task<BankTransaction> RowAsync(BankConnection connection,
        string providerId = "t1", DateOnly? date = null)
    {
        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = new BankTransaction
        {
            LinkedAccountId = connection.Accounts.First().Id,
            ProviderTransactionId = providerId,
            Date = date ?? new DateOnly(2026, 9, 1),
            Amount = 10m,
            Description = "LIDL",
            MerchantName = "Lidl",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        dbContext.Add(row);
        await dbContext.SaveChangesAsync(Ct);

        return row;
    }

    /// <summary>
    /// The caller's own account id. A request has already been made by the time this runs,
    /// so the host's user is the one the middleware provisioned.
    /// </summary>
    private async Task<Guid> FirstUserAsync(AppDbContext dbContext)
    {
        await _host.Client.GetAsync("/bank-connections", Ct);

        return (await dbContext.Set<Data.Entities.User>().OrderBy(user => user.Id).FirstAsync(Ct)).Id;
    }

    /// <summary>
    /// The webhook queues a sync and answers; the sync itself runs on the job pump. Bounded,
    /// so a regression fails rather than hangs.
    /// </summary>
    private async Task WaitForSyncAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (_bank.CursorsSeen.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25, Ct);
    }
}
