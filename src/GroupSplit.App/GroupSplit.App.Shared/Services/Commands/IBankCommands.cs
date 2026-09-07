using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to do with a bank, in one place.
/// </summary>
/// <remarks>
/// Like the other command interfaces: each call runs through the error presenter so a
/// refusal is shown once and the caller is handed a false rather than an exception, says
/// what happened to what, and announces itself so the page states catch up.
/// <para>
/// Filing raises both announcements, because it is the one action that is two things: an
/// imported row leaves the inbox, and an expense appears in a group.
/// </para>
/// </remarks>
public interface IBankCommands
{
    /// <summary>
    /// Asks the API for a token that opens the provider's linking UI, naming a connection
    /// to repair one whose bank wants a fresh sign-in.
    /// </summary>
    Task<LinkTokenResponse?> CreateLinkTokenAsync(Guid? connectionId = null, CancellationToken ct = default);

    /// <summary>Exchanges what the linking UI handed back for a stored connection.</summary>
    Task<BankConnectionResponse?> LinkAsync(string publicToken, CancellationToken ct = default);

    Task<bool> UnlinkAsync(Guid connectionId, string institutionName, CancellationToken ct = default);

    /// <summary>Asks for a sync. It runs in the background, so this says so rather than claiming it is done.</summary>
    Task<bool> SyncAsync(Guid connectionId, string institutionName, CancellationToken ct = default);

    Task<TransactionResponse?> FileAsync(Guid rowId, FileBankTransactionRequest request, string destination,
        CancellationToken ct = default);

    Task<bool> IgnoreAsync(Guid rowId, string title, CancellationToken ct = default);

    Task<bool> RestoreAsync(Guid rowId, string title, CancellationToken ct = default);
}
