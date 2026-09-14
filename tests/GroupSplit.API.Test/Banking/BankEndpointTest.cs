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
        var refresh = await stranger.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);
        var unlink = await stranger.DeleteAsync($"/bank-connections/{connection.Id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, sync.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlink.StatusCode);

        // Not merely refused: the stranger's refresh must not have reached the bank either,
        // which is the difference between a scoped query and a check made after the work.
        Assert.Equal(0, _bank.AccountReads);
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
        Assert.Equal(BankConnectionStatus.Active, await StatusAsync(connection.Id));
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

    /// <summary>
    /// A bank saying the sign-in is repaired is the only way a connection gets back to
    /// Active, so the status it writes has to survive the request.
    /// </summary>
    /// <remarks>
    /// Repairing opens the provider's UI in update mode, which hands back no public token,
    /// so nothing on the linking path runs and nothing else in the application clears
    /// LoginRequired. A status left in the change tracker meant somebody who had just
    /// signed in again was told to sign in again -- and would be told that for ever, with
    /// unlinking and linking afresh, at the cost of another provider item, the only way out.
    /// </remarks>
    [Fact]
    public async Task A_repaired_sign_in_is_stored_and_the_connection_can_sync_again()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        await SetStatusAsync(connection.Id, BankConnectionStatus.LoginRequired);

        _bank.Answer("cursor-one");
        _bank.Webhook = _ => new LoginRepaired("item-fake");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

        Assert.Equal(BankConnectionStatus.Active, await StatusAsync(connection.Id));

        // And the sync the repair asks for is not refused for the status it just cleared:
        // it is dispatched to a worker that reads the connection afresh, so the save has to
        // have happened first.
        await WaitForSyncAsync();
        Assert.NotEmpty(_bank.CursorsSeen);
    }

    [Fact]
    public async Task Refreshing_a_bank_connection_reads_accounts_updates_status_and_queues_a_sync()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");
        await SetStatusAsync(connection.Id, BankConnectionStatus.LoginRequired);

        _bank.Answer("cursor-refresh");
        _bank.AnswerAccounts(
        [
            new ImportedAccount("acc-1", "Everyday (Updated)", "1234", "depository", "checking", "USD"),
            new ImportedAccount("acc-2", "Savings", "5678", "depository", "savings", "USD")
        ]);

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var refreshed = await response.Content.ReadFromJsonAsync<BankConnectionResponse>(Json, Ct);
        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed.Accounts.Count);
        Assert.Equal(BankConnectionState.Active, refreshed.Status);

        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await dbContext.Set<BankConnection>()
            .Include(c => c.Accounts)
            .FirstAsync(c => c.Id == connection.Id, Ct);

        Assert.Equal(BankConnectionStatus.Active, stored.Status);
        Assert.Equal(2, stored.Accounts.Count);
        Assert.Contains(stored.Accounts, a => a.ProviderAccountId == "acc-1" && a.Name == "Everyday (Updated)");
        Assert.Contains(stored.Accounts, a => a.ProviderAccountId == "acc-2" && a.Name == "Savings");

        await WaitForSyncAsync();
        Assert.NotEmpty(_bank.CursorsSeen);
    }

    /// <summary>
    /// The provider cannot hand over an account nobody shared, so this notification is not
    /// a cue to go and read the account list -- it is a cue to put the person in front of
    /// update mode, which is the only thing that can resolve it.
    /// </summary>
    [Fact]
    public async Task A_new_accounts_available_webhook_flags_the_connection_and_asks_the_provider_nothing()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Webhook = _ => new NewAccountsAvailable("item-fake");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        Assert.True(stored.AccountsNotShared);
        Assert.Equal(BankConnectionStatus.Active, stored.Status);
        Assert.Equal(0, _bank.AccountReads);

        // And it reaches the person, which is the whole of the difference from a log line.
        var mine = await MineAsync(connection.Id);
        Assert.True(mine.AccountsNotShared);
        Assert.True(mine.NeedsAttentionSoon);
    }

    [Theory]
    [InlineData("the consent is about to expire")]
    [InlineData("the institution is being migrated")]
    public async Task A_warning_that_the_sign_in_will_expire_is_shown_without_stopping_the_syncs(string reason)
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Webhook = _ => new SignInWillExpire("item-fake", reason, DateTimeOffset.UtcNow.AddDays(7));

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        Assert.True(stored.SignInExpiring);

        // Nothing has broken yet: an item that still works must go on syncing, or a warning
        // about a future outage becomes one now.
        Assert.Equal(BankConnectionStatus.Active, stored.Status);
        Assert.True((await MineAsync(connection.Id)).NeedsAttentionSoon);
    }

    [Fact]
    public async Task Signing_in_again_clears_the_warning_that_it_was_about_to_expire()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Answer("cursor-repaired");
        _bank.Webhook = _ => new SignInWillExpire("item-fake", "the consent is about to expire", null);

        using var anonymous = _host.AnonymousClient();
        using var warning = Signed("{}");
        await anonymous.SendAsync(warning, Ct);

        _bank.Webhook = _ => new LoginRepaired("item-fake");

        using var repaired = Signed("{}");
        await anonymous.SendAsync(repaired, Ct);

        Assert.False((await ConnectionAsync(connection.Id)).SignInExpiring);
    }

    [Fact]
    public async Task Access_withdrawn_from_one_account_marks_that_account_and_leaves_the_rest_working()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");
        await AddAccountAsync(connection.Id, "acc-2", "Savings");

        _bank.Webhook = _ => new AccountAccessRevoked("item-fake", "acc-2");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        Assert.Equal(BankConnectionStatus.Active, stored.Status);
        Assert.True(stored.Accounts.Single(a => a.ProviderAccountId == "acc-2").AccessRevoked);
        Assert.False(stored.Accounts.Single(a => a.ProviderAccountId == "acc-1").AccessRevoked);

        var mine = await MineAsync(connection.Id);
        Assert.True(mine.Accounts.Single(a => a.Name == "Savings").AccessRevoked);
    }

    /// <summary>
    /// The other direction of update mode: somebody unshares an account rather than
    /// sharing one.
    /// </summary>
    /// <remarks>
    /// The account is kept, because rows point at it and it still explains where last
    /// month's coffee came from. What it must not do is go on looking healthy: listed
    /// beside the accounts still importing, with nothing to tell them apart, it is the
    /// same silence issue 233 was about seen from the opposite end -- nothing will ever
    /// arrive for it again and nothing says so.
    /// </remarks>
    [Fact]
    public async Task An_account_unshared_at_the_bank_is_kept_but_stops_looking_healthy()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");
        await AddAccountAsync(connection.Id, "acc-2", "Savings");

        _bank.Answer("cursor-unshared");

        // Update mode, with Savings unticked. The provider stops reporting it.
        _bank.AnswerAccounts(
        [
            new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD")
        ]);

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        // Kept: two accounts, not one.
        Assert.Equal(2, stored.Accounts.Count);

        Assert.True(stored.Accounts.Single(a => a.ProviderAccountId == "acc-2").AccessRevoked);
        Assert.False(stored.Accounts.Single(a => a.ProviderAccountId == "acc-1").AccessRevoked);

        // And the person can see which is which.
        var mine = await MineAsync(connection.Id);
        Assert.True(mine.Accounts.Single(a => a.Name == "Savings").AccessRevoked);
        Assert.False(mine.Accounts.Single(a => a.Name == "Everyday").AccessRevoked);
    }

    /// <summary>
    /// The provider reporting the account again is the whole of the evidence that it was
    /// shared back: a revoked account stops being reported at all.
    /// </summary>
    [Fact]
    public async Task Sharing_a_withdrawn_account_back_clears_the_mark_on_it()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");
        await AddAccountAsync(connection.Id, "acc-2", "Savings");

        _bank.Webhook = _ => new AccountAccessRevoked("item-fake", "acc-2");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");
        await anonymous.SendAsync(request, Ct);

        _bank.Answer("cursor-reshared");
        _bank.AnswerAccounts(
        [
            new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD"),
            new ImportedAccount("acc-2", "Savings", "5678", "depository", "savings", "USD")
        ]);

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ConnectionAsync(connection.Id);
        Assert.False(stored.Accounts.Single(a => a.ProviderAccountId == "acc-2").AccessRevoked);
    }

    /// <summary>
    /// A refresh that the bank refuses because it wants a new sign-in is not the provider
    /// having a bad minute, and must not be reported as one -- the person would be told to
    /// try again shortly, for ever.
    /// </summary>
    [Fact]
    public async Task Refreshing_a_bank_that_wants_a_new_sign_in_says_so_and_marks_the_connection()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.RefuseAccountsWith = BankSyncFailure.LoginRequired;

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal(ErrorCodes.BankConnectionNeedsAttention, problem.GetProperty("code").GetString());

        // Marked, or the card offers nothing to press.
        Assert.Equal(BankConnectionStatus.LoginRequired, (await ConnectionAsync(connection.Id)).Status);
    }

    [Fact]
    public async Task Refreshing_clears_the_flag_that_an_account_was_not_shared()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");
        await FlagNotSharedAsync(connection.Id);

        _bank.Answer("cursor-shared");
        _bank.AnswerAccounts(
        [
            new ImportedAccount("acc-1", "Everyday", "1234", "depository", "checking", "USD"),
            new ImportedAccount("acc-2", "Savings", "5678", "depository", "savings", "USD")
        ]);

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        Assert.False(stored.AccountsNotShared);
        Assert.Equal(2, stored.Accounts.Count);
    }

    [Fact]
    public async Task A_bank_asking_for_a_fresh_sign_in_is_stored()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Webhook = _ => new LoginRequired("item-fake");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);
        Assert.Equal(BankConnectionStatus.LoginRequired, await StatusAsync(connection.Id));
    }

    /// <summary>
    /// Somebody withdrawing this app's access at their bank is the one notification that
    /// cannot be recovered from, so losing it tells them the wrong story ever after.
    /// </summary>
    [Fact]
    public async Task Access_withdrawn_at_the_bank_is_stored()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Webhook = _ => new PermissionRevoked("item-fake");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);
        Assert.Equal(BankConnectionStatus.Revoked, await StatusAsync(connection.Id));
    }

    /// <summary>
    /// The one caller-written value on the only anonymous route. Unconstrained it reached a
    /// log sink verbatim, newlines and all.
    /// </summary>
    /// <remarks>
    /// The bare newline is the case worth naming. A pattern anchored with <c>$</c> admits a
    /// single trailing one -- that is what <c>$</c> means in .NET -- so the constraint has to
    /// be anchored with <c>\z</c>, and a test that only offers something obviously wrong
    /// like a space would pass either way and guard nothing.
    /// </remarks>
    [Theory]
    // A bare trailing newline: the case a "$"-anchored pattern lets through.
    [InlineData("fake%0A")]
    // A whole forged log line.
    [InlineData("fake%0A2026-01-01%20WARN%20not-a-real-log-line")]
    // A connector key is lower-case by contract, and route constraints match IgnoreCase.
    [InlineData("FAKE")]
    public async Task A_webhook_path_that_could_not_name_a_connector_is_refused_by_routing(string segment)
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.PostAsync($"/webhooks/{segment}", Body("{}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Refused by routing, before any of this route's own code runs -- so nothing it says
        // can carry the segment anywhere.
        Assert.DoesNotContain(_host.Logs, entry => entry.Message.Contains("which nothing here speaks"));
    }

    /// <summary>
    /// A link request with no token is refused before anything is written down.
    /// </summary>
    /// <remarks>
    /// Worth asserting over HTTP rather than trusting the annotation: linking writes a row
    /// holding the value, encrypted, and commits it before it asks the provider anything.
    /// Unvalidated, an absent token was a null through the protector and an opaque 500 on a
    /// route that advertises a validation problem, and an unbounded one was a row of
    /// whatever the body carried.
    /// <para>
    /// It is also the first test in this suite that exercises a DataAnnotation at all. .NET
    /// 10 validation is a source-generated interceptor on the AddValidation() call site, so
    /// while this host called it from the test assembly the annotations quietly did nothing
    /// here; it goes through GroupSplit.API's own AddApiValidation() now, which is what makes
    /// this assertable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Linking_with_no_public_token_is_refused_rather_than_written_down()
    {
        var response = await _host.Client.PostAsync("/bank-connections", Body("{}"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await dbContext.Set<PendingBankLink>().ToListAsync(Ct));
        Assert.Empty(await dbContext.Set<BankConnection>().ToListAsync(Ct));
    }

    private async Task<List<BankConnection>> ConnectionsAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>()
            .AsNoTracking()
            .ToListAsync(Ct);
    }

    /// <summary>The connection as it is stored, with its accounts and nothing tracked.</summary>
    private async Task<BankConnection> ConnectionAsync(Guid connectionId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .AsNoTracking()
            .FirstAsync(connection => connection.Id == connectionId, Ct);
    }

    /// <summary>The same connection as the person's own client sees it.</summary>
    private async Task<BankConnectionResponse> MineAsync(Guid connectionId)
    {
        var response = await _host.Client.GetFromJsonAsync<BankConnectionsResponse>("/bank-connections", Json, Ct);

        Assert.NotNull(response);

        return response.Connections.Single(connection => connection.Id == connectionId);
    }

    private async Task AddAccountAsync(Guid connectionId, string providerAccountId, string name)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.Add(new LinkedAccount
        {
            BankConnectionId = connectionId,
            ProviderAccountId = providerAccountId,
            Name = name,
            Type = "depository"
        });

        await dbContext.SaveChangesAsync(Ct);
    }

    private async Task FlagNotSharedAsync(Guid connectionId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = await dbContext.Set<BankConnection>().FirstAsync(row => row.Id == connectionId, Ct);
        connection.AccountsNotShared = true;

        await dbContext.SaveChangesAsync(Ct);
    }

    private async Task SetStatusAsync(Guid connectionId, BankConnectionStatus status)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = await dbContext.Set<BankConnection>().FirstAsync(row => row.Id == connectionId, Ct);
        connection.Status = status;

        await dbContext.SaveChangesAsync(Ct);
    }

    private async Task<BankConnectionStatus> StatusAsync(Guid connectionId)
    {
        await using var scope = _host.Services.CreateAsyncScope();

        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<BankConnection>()
            .AsNoTracking()
            .FirstAsync(row => row.Id == connectionId, Ct)).Status;
    }

    /// <summary>
    /// A sync asked for on a connection the bank has stopped talking to. Refused here
    /// rather than queued, because a job would read the same status, answer NotSyncable
    /// and tell nobody -- the person would watch a spinner and get no explanation.
    /// </summary>
    [Theory]
    [InlineData(BankConnectionStatus.LoginRequired)]
    [InlineData(BankConnectionStatus.Revoked)]
    public async Task Syncing_a_connection_that_needs_the_person_is_refused_and_says_so(BankConnectionStatus status)
    {
        var connection = await LinkAsync();
        await SetStatusAsync(connection.Id, status);

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/sync", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal(ErrorCodes.BankConnectionNeedsAttention, problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// A connection whose provider this deployment no longer speaks -- one switched off,
    /// one removed, or the seeder's demo data. It still has to be removable, or the row is
    /// unremovable for ever.
    /// </summary>
    [Fact]
    public async Task A_connection_whose_provider_is_not_configured_still_unlinks()
    {
        var connection = await LinkAsync(provider: "a-provider-nothing-here-speaks");

        var response = await _host.Client.DeleteAsync($"/bank-connections/{connection.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ConnectionsAsync());

        Assert.Contains(_host.Logs, entry => entry.Message.Contains("no connector is registered for it"));
    }

    /// <summary>
    /// Access withdrawn from an account this connection never had. Nothing can be marked,
    /// but the provider has just confirmed the bank has an account that is not ours -- the
    /// same situation the flag exists for.
    /// </summary>
    [Fact]
    public async Task Access_withdrawn_from_an_account_we_never_had_flags_the_connection_instead()
    {
        var connection = await LinkAsync(providerItemId: "item-fake");

        _bank.Webhook = _ => new AccountAccessRevoked("item-fake", "acc-never-shared");

        using var anonymous = _host.AnonymousClient();
        using var request = Signed("{}");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

        var stored = await ConnectionAsync(connection.Id);

        Assert.True(stored.AccountsNotShared);
        Assert.DoesNotContain(stored.Accounts, account => account.AccessRevoked);
    }

    /// <summary>
    /// A stored token the key ring can no longer open. Three readers meet it and each
    /// answers differently on purpose; none of them may answer with a bare 500, and until
    /// now nothing held them to that.
    /// </summary>
    /// <remarks>
    /// It is a state a deployment can really be in -- a certificate replaced, a row edited
    /// -- and the whole point of handling it is that the person is told what to do instead
    /// of being shown a trace id.
    /// </remarks>
    [Fact]
    public async Task A_connection_whose_token_cannot_be_read_cannot_be_repaired_and_says_why()
    {
        var connection = await LinkAsync(accessTokenCiphertext: "not-a-protected-token");

        var response = await _host.Client.PostAsJsonAsync(
            "/bank-connections/link-token", new LinkTokenRequest { ConnectionId = connection.Id }, Json, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal(ErrorCodes.BankConnectionUnrecoverable, problem.GetProperty("code").GetString());

        // Marked, so the card stops offering a repair that cannot work and offers the one
        // way out instead.
        Assert.Equal(BankConnectionStatus.LoginRequired, (await ConnectionAsync(connection.Id)).Status);
    }

    [Fact]
    public async Task A_connection_whose_token_cannot_be_read_refuses_a_refresh_and_says_why()
    {
        var connection = await LinkAsync(accessTokenCiphertext: "not-a-protected-token");

        var response = await _host.Client.PostAsync($"/bank-connections/{connection.Id}/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal(ErrorCodes.BankConnectionNeedsAttention, problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// Unlinking is the one reader that carries on. The other two refuse and both point
    /// here as the way out, so throwing would make the way out the one thing that does not
    /// work -- and the item is stranded at the provider either way.
    /// </summary>
    [Fact]
    public async Task A_connection_whose_token_cannot_be_read_still_unlinks_and_says_what_was_left_behind()
    {
        var connection = await LinkAsync(accessTokenCiphertext: "not-a-protected-token", providerItemId: "item-stranded");

        var response = await _host.Client.DeleteAsync($"/bank-connections/{connection.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Gone from here, and never mentioned to the provider -- there was nothing to
        // mention it with.
        Assert.Empty(await ConnectionsAsync());
        Assert.Empty(_bank.RemovedTokens);

        // The item number is in the log, because it is all that is left of it.
        Assert.Contains(_host.Logs, entry =>
            entry.Message.Contains("has to be removed by hand") && entry.Message.Contains("item-stranded"));
    }

    /// <summary>
    /// The four inbox routes nothing reached over HTTP: their binding, their status codes
    /// and the round trip between them.
    /// </summary>
    /// <remarks>
    /// Worth having at this level rather than against the service, for the same reason the
    /// span test below is: <c>withDuplicates</c> is bound from the query string, and a
    /// nullable bool that stops binding is exactly what a test calling the service directly
    /// cannot see.
    /// </remarks>
    [Fact]
    public async Task The_inbox_summary_counts_what_is_waiting()
    {
        var connection = await LinkAsync();

        await RowAsync(connection, providerId: "one");
        await RowAsync(connection, providerId: "two");

        var summary = await _host.Client.GetFromJsonAsync<InboxSummaryResponse>("/inbox/summary", Json, Ct);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.NewCount);

        // Not asked for, so not answered: counting possible duplicates is the expensive
        // half and a client that did not want it must not pay for it.
        Assert.Null(summary.PossibleDuplicates);
    }

    [Fact]
    public async Task The_inbox_summary_counts_possible_duplicates_when_it_is_asked_to()
    {
        var connection = await LinkAsync();
        await RowAsync(connection, providerId: "one");

        var summary = await _host.Client.GetFromJsonAsync<InboxSummaryResponse>(
            "/inbox/summary?withDuplicates=true", Json, Ct);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.NewCount);
        Assert.Equal(0, summary.PossibleDuplicates);
    }

    [Fact]
    public async Task A_row_can_be_asked_what_it_might_already_be()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        var response = await _host.Client.GetAsync($"/inbox/{row.Id}/matches", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var matches = await response.Content.ReadFromJsonAsync<List<ExpenseMatchResponse>>(Json, Ct);
        Assert.NotNull(matches);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task Asking_what_somebody_elses_row_might_be_is_a_404()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        using var stranger = _host.ClientForAnotherUser();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/inbox/{row.Id}/matches", Ct)).StatusCode);
    }

    /// <summary>
    /// Ignoring takes the row out of the list without deleting it, and restoring puts it
    /// back -- the undo the inbox promises.
    /// </summary>
    [Fact]
    public async Task Ignoring_a_row_takes_it_out_of_the_inbox_and_restoring_brings_it_back()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        var ignored = await _host.Client.PostAsync($"/inbox/{row.Id}/ignore", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, ignored.StatusCode);

        Assert.Empty(await ItemsAsync("/inbox"));

        var restored = await _host.Client.PostAsync($"/inbox/{row.Id}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);

        Assert.Single(await ItemsAsync("/inbox"));
    }

    [Fact]
    public async Task Restoring_a_row_that_was_never_ignored_changes_nothing_and_still_answers()
    {
        var connection = await LinkAsync();
        var row = await RowAsync(connection);

        var response = await _host.Client.PostAsync($"/inbox/{row.Id}/restore", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await ItemsAsync("/inbox"));
    }

    [Fact]
    public async Task Ignoring_or_restoring_a_row_that_is_not_there_is_a_404()
    {
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await _host.Client.PostAsync($"/inbox/{missing}/ignore", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _host.Client.PostAsync($"/inbox/{missing}/restore", null, Ct)).StatusCode);
    }

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

    private async Task<BankConnection> LinkAsync(
        string? providerItemId = null, string? accessTokenCiphertext = null, string? provider = null)
    {
        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>();

        // The host's own user, provisioned by the first request its client made.
        var me = await FirstUserAsync(dbContext);

        var connection = new BankConnection
        {
            UserId = me,
            Provider = provider ?? FakeBankConnector.Name,
            ProviderItemId = providerItemId ?? $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            // A ciphertext given verbatim is one the key ring cannot open, which is what
            // the three readers of a stored token each have to cope with.
            AccessTokenCiphertext = accessTokenCiphertext ?? protector.Protect(FakeBankConnector.AccessToken),
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

    /// <summary>
    /// Newest first means newest by the date on the rows. A charge authorized on the 25th of
    /// September that posted on the 2nd of October is shown as the 25th, so it belongs below
    /// the row showing the 28th -- ordering by the posting date put it at the top of a list
    /// where nothing on screen said why.
    /// </summary>
    [Fact]
    public async Task The_inbox_orders_by_the_date_it_shows()
    {
        var connection = await LinkAsync();

        await RowAsync(connection, providerId: "posted-in-october",
            date: new DateOnly(2026, 10, 2), authorizedDate: new DateOnly(2026, 9, 25));

        await RowAsync(connection, providerId: "newest", date: new DateOnly(2026, 9, 28));
        await RowAsync(connection, providerId: "oldest", date: new DateOnly(2026, 9, 20));

        var september = await ItemsAsync("/inbox?From=2026-09-01&To=2026-09-30");

        Assert.Equal(["2026-09-28", "2026-09-25", "2026-09-20"], SpentOn(september));
    }

    /// <summary>
    /// The same order, read a page at a time: every row once, and the page boundary falling
    /// where the dates on the page say it should. A sort the database cannot separate two
    /// rows by is a row that arrives twice or never, so the tie-break has to hold across
    /// requests as well as within one.
    /// </summary>
    [Fact]
    public async Task Paging_a_narrowed_span_returns_each_row_once_in_that_order()
    {
        var connection = await LinkAsync();

        await RowAsync(connection, providerId: "posted-in-october",
            date: new DateOnly(2026, 10, 2), authorizedDate: new DateOnly(2026, 9, 25));

        // Two rows on one day, which only the tie-break can order.
        await RowAsync(connection, providerId: "same-day-a", date: new DateOnly(2026, 9, 28));
        await RowAsync(connection, providerId: "same-day-b", date: new DateOnly(2026, 9, 28));
        await RowAsync(connection, providerId: "oldest", date: new DateOnly(2026, 9, 20));

        var whole = await ItemsAsync("/inbox?From=2026-09-01&To=2026-09-30");

        var paged = new List<JsonElement>();

        for (var page = 1; page <= 4; page++)
            paged.AddRange(await ItemsAsync($"/inbox?From=2026-09-01&To=2026-09-30&Page={page}&PageSize=1"));

        Assert.Equal(4, whole.Count);
        Assert.Equal(["2026-09-28", "2026-09-28", "2026-09-25", "2026-09-20"], SpentOn(paged));
        Assert.Equal(Ids(whole), Ids(paged));
        Assert.Equal(4, Ids(paged).Distinct().Count());
    }

    private static List<string?> SpentOn(IEnumerable<JsonElement> rows) =>
        rows.Select(row => row.GetProperty("spentOn").GetString()).ToList();

    private static List<string?> Ids(IEnumerable<JsonElement> rows) =>
        rows.Select(row => row.GetProperty("id").GetString()).ToList();

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
        string providerId = "t1", DateOnly? date = null, DateOnly? authorizedDate = null)
    {
        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = new BankTransaction
        {
            LinkedAccountId = connection.Accounts.First().Id,
            ProviderTransactionId = providerId,
            Date = date ?? new DateOnly(2026, 9, 1),
            AuthorizedDate = authorizedDate,
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
