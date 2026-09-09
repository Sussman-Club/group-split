using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="IBankCommands"/>
public sealed class BankCommands(
    IBankConnectionsClient connections,
    IInboxClient inbox,
    ApiErrorPresenter errors,
    ISnackbar snackbar,
    DataChangeNotifier changes) : IBankCommands
{
    public async Task<LinkTokenResponse?> CreateLinkTokenAsync(Guid? connectionId = null,
        CancellationToken ct = default)
    {
        LinkTokenResponse? token = null;

        var done = await errors.TryAsync(async () =>
        {
            token = await connections.CreateLinkTokenAsync(new LinkTokenRequest { ConnectionId = connectionId }, ct);
        }, "Could not start the bank connection.");

        return done ? token : null;
    }

    public async Task<BankConnectionResponse?> LinkAsync(string publicToken, CancellationToken ct = default)
    {
        BankConnectionResponse? linked = null;

        var done = await errors.TryAsync(async () =>
        {
            linked = await connections.LinkBankConnectionAsync(
                new CreateBankConnectionRequest { PublicToken = publicToken }, ct);

            // The first sync is queued by the API, so what has actually happened is the
            // connection, and saying more than that would be a promise about timing.
            snackbar.Add($"{linked.InstitutionName} linked. Your transactions will appear shortly.", Severity.Success);

            await changes.NotifyBankDataChangedAsync();
        }, "Could not link the bank.");

        return done ? linked : null;
    }

    public Task<bool> UnlinkAsync(Guid connectionId, string institutionName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await connections.UnlinkBankConnectionAsync(connectionId, ct);

            // Worth saying, because the expenses staying is the part somebody would worry
            // about before pressing this.
            snackbar.Add($"{institutionName} unlinked. Expenses you already added are unaffected.", Severity.Success);

            await changes.NotifyBankDataChangedAsync();
        }, "Could not unlink the bank.");

    public Task<bool> SyncAsync(Guid connectionId, string institutionName, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await connections.SyncBankConnectionAsync(connectionId, ct);
            snackbar.Add($"Checking {institutionName} for new transactions.", Severity.Info);
        }, "Could not check for new transactions.");

    public async Task<TransactionResponse?> FileAsync(Guid rowId, FileBankTransactionRequest request,
        string destination, CancellationToken ct = default)
    {
        TransactionResponse? expense = null;

        var done = await errors.TryAsync(async () =>
        {
            expense = await inbox.FileBankTransactionAsync(rowId, request, ct);
            snackbar.Add($"{expense.Name} added to {destination}.", Severity.Success);

            // Both: a row left the inbox, and an expense appeared in a group.
            await changes.NotifyBankDataChangedAsync();
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not add the expense.");

        return done ? expense : null;
    }

    public async Task<TransactionResponse?> AttachAsync(Guid rowId, Guid transactionId,
        CancellationToken ct = default)
    {
        TransactionResponse? expense = null;

        var done = await errors.TryAsync(async () =>
        {
            expense = await inbox.LinkBankTransactionAsync(rowId, new LinkBankTransactionRequest
            {
                TransactionId = transactionId
            }, ct);

            // Worth saying plainly, because the thing being confirmed is that there is
            // still only one expense.
            snackbar.Add($"Attached to {expense.Name}. It is still one expense.", Severity.Success);

            await changes.NotifyBankDataChangedAsync();
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not attach it to that expense.");

        return done ? expense : null;
    }

    public Task<bool> DismissMatchAsync(Guid rowId, Guid transactionId, string title,
        CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await inbox.DismissBankTransactionMatchAsync(rowId, new DismissBankMatchRequest
            {
                TransactionId = transactionId
            }, ct);

            snackbar.Add($"{title} is a separate payment. We will not suggest that one again.", Severity.Success);

            await changes.NotifyBankDataChangedAsync();
        }, "Could not put that suggestion away.");

    public Task<bool> IgnoreAsync(Guid rowId, string title, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await inbox.IgnoreBankTransactionAsync(rowId, ct);
            snackbar.Add($"{title} ignored.", Severity.Success);
            await changes.NotifyBankDataChangedAsync();
        }, "Could not ignore it.");

    public Task<bool> RestoreAsync(Guid rowId, string title, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await inbox.RestoreBankTransactionAsync(rowId, ct);
            snackbar.Add($"{title} is back in your inbox.", Severity.Success);
            await changes.NotifyBankDataChangedAsync();
        }, "Could not restore it.");

    public Task<int> IgnoreManyAsync(IReadOnlyList<(Guid Id, string Title)> rows,
        CancellationToken ct = default) =>
        ManyAsync(rows.Count,
            index => inbox.IgnoreBankTransactionAsync(rows[index].Id, ct),
            done => done == 1
                ? $"{rows[0].Title} ignored."
                : $"{done} transactions ignored.",
            "Could not ignore them.");

    public Task<int> KeepPersonalManyAsync(IReadOnlyList<(Guid Id, string Title, bool FileAnyway)> rows,
        CancellationToken ct = default) =>
        ManyAsync(rows.Count,
            index => inbox.FileBankTransactionAsync(
                rows[index].Id,
                new FileBankTransactionRequest { FileAnyway = rows[index].FileAnyway },
                ct),
            done => done == 1
                ? $"{rows[0].Title} added to your own expenses."
                : $"{done} transactions added to your own expenses.",
            "Could not add them.");

    /// <summary>
    /// Runs the same write over several rows, and tells the person once.
    /// </summary>
    /// <remarks>
    /// One announcement at the end rather than one per row: every state that holds inbox
    /// rows re-reads itself off that announcement, and twenty of them would be twenty round
    /// trips behind a page nobody is looking at yet.
    /// <para>
    /// A row that fails does not stop the rest, and it does not raise its own message
    /// either. What comes back is how many landed; the caller says what to do about the
    /// difference, because only it knows what was asked for.
    /// </para>
    /// </remarks>
    private async Task<int> ManyAsync(int count, Func<int, Task> write, Func<int, string> said,
        string whenNoneLanded)
    {
        var done = 0;

        for (var index = 0; index < count; index++)
        {
            try
            {
                await write(index);
                done++;
            }
            catch (Exception exception) when (ApiErrors.IsApiFailure(exception))
            {
                // Kept, and reported by the count rather than by a message of its own. A
                // row the API refuses -- one already filed on another tab, one it will not
                // file over a duplicate -- is an ordinary outcome in a queue somebody is
                // clearing in bulk.
            }
        }

        if (done > 0)
        {
            snackbar.Add(said(done), Severity.Success);
            await changes.NotifyBankDataChangedAsync();
        }
        else if (count > 0)
        {
            snackbar.Add(whenNoneLanded, Severity.Error);
        }

        return done;
    }
}
