using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to an expense, and the one read that belongs with them. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
public interface ITransactionCommands
{
    Task<bool> CreateAsync(CreateTransactionRequest request, CancellationToken ct = default);

    Task<bool> UpdateAsync(Guid transactionId, JsonPatchDocument<UpdateTransactionRequest> patch,
        string name, CancellationToken ct = default);

    /// <summary>
    /// Removes a transaction and says so.
    /// </summary>
    /// <param name="noun">
    /// What to call it if the removal fails -- "expense", or "settlement" from the one
    /// listing that shows those. The success line uses <paramref name="name"/>, which
    /// already reads as itself; only the failure has to name the kind, and reporting that a
    /// settlement could not be deleted as a failure to delete an expense sent people to
    /// look for an expense that was never there.
    /// </param>
    Task<bool> DeleteAsync(Guid transactionId, string name, string noun = "expense",
        CancellationToken ct = default);

    /// <summary>
    /// What the expense described would be divided into, asked of the API rather than
    /// worked out here.
    /// </summary>
    /// <remarks>
    /// A read, and it lives here because it is the same call the save is about to make and
    /// has to agree with it to the cent. Refusals are handed back rather than shown: the
    /// dialog puts "8.00 left to assign" beside the shares, and a snackbar for every
    /// keystroke that does not yet add up would be a stream of them.
    /// </remarks>
    Task<SplitPreviewResponse?> PreviewAsync(CreateTransactionRequest request,
        CancellationToken ct = default);
}
