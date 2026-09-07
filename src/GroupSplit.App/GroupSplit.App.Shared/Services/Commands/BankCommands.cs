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
}
