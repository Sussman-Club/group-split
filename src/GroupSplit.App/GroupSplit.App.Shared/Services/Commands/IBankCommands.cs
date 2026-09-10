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

    /// <summary>Refreshes accounts on an existing connection and asks for a sync.</summary>
    Task<BankConnectionResponse?> RefreshAsync(Guid connectionId, string institutionName, CancellationToken ct = default);

    /// <summary>Asks for a sync. It runs in the background, so this says so rather than claiming it is done.</summary>
    Task<bool> SyncAsync(Guid connectionId, string institutionName, CancellationToken ct = default);

    Task<TransactionResponse?> FileAsync(Guid rowId, FileBankTransactionRequest request, string destination,
        CancellationToken ct = default);

    /// <summary>
    /// Points an imported row at an expense that is already recorded, instead of filing it
    /// as a second one. One expense is left, carrying the bank's row.
    /// </summary>
    Task<TransactionResponse?> AttachAsync(Guid rowId, Guid transactionId, CancellationToken ct = default);

    /// <summary>
    /// Says a suggested pair is not the same money, so it is not suggested again.
    /// </summary>
    Task<bool> DismissMatchAsync(Guid rowId, Guid transactionId, string title, CancellationToken ct = default);

    Task<bool> IgnoreAsync(Guid rowId, string title, CancellationToken ct = default);

    Task<bool> RestoreAsync(Guid rowId, string title, CancellationToken ct = default);

    /// <summary>
    /// Ignores several rows at once.
    /// </summary>
    /// <remarks>
    /// One announcement and one message for the lot, which is the only reason this is not
    /// the caller looping over <see cref="IgnoreAsync"/>: twenty rows that way is twenty
    /// snackbars stacked over the page and twenty re-reads of the inbox behind it.
    /// <para>
    /// A row that fails does not stop the rest. The inbox is a queue and the point of
    /// clearing several at once is that most of them go; the count that comes back says how
    /// many did.
    /// </para>
    /// </remarks>
    /// <returns>How many were ignored.</returns>
    Task<int> IgnoreManyAsync(IReadOnlyList<(Guid Id, string Title)> rows, CancellationToken ct = default);

    /// <summary>
    /// Files several rows as the caller's own expenses -- in no group, owed to nobody.
    /// </summary>
    /// <param name="rows">
    /// Each row's id, its title, and whether it is being filed over a suspected duplicate.
    /// That last is the person's answer rather than a default: a row showing a suggestion is
    /// only ever in this list because they ticked it themselves.
    /// </param>
    /// <returns>How many became expenses.</returns>
    Task<int> KeepPersonalManyAsync(IReadOnlyList<(Guid Id, string Title)> rows,
        CancellationToken ct = default);
}
