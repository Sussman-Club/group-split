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

    /// <summary>
    /// The same, for an expense that already exists.
    /// </summary>
    /// <remarks>
    /// Not <see cref="PreviewAsync"/> with the edited values: an edit is divided again by
    /// the version of the rule the expense was written under, so a preview built as though
    /// it were a new expense would show today's rule and the save would then produce
    /// different numbers.
    /// </remarks>
    /// <param name="redivide">
    /// True when the person has asked for the division to be worked out again, which is the
    /// dialog's "Automatically". False -- the default -- previews what an edit that says
    /// nothing about the shares does, which is keep them.
    /// </param>
    Task<SplitPreviewResponse?> PreviewUpdateAsync(Guid transactionId, UpdateTransactionRequest request,
        bool redivide = false, CancellationToken ct = default);

    /// <summary>
    /// Records what produced an expense's shares: a version of a split rule, or nobody --
    /// the amounts are the expense's own.
    /// </summary>
    /// <remarks>
    /// Provenance and nothing else; not one share moves. What it changes is what a later
    /// edit does, so it announces like any other write -- an expense recorded as a rule's
    /// follows that rule when its amount or payer changes, and one whose shares are its own
    /// has no rule to be re-billed under.
    /// </remarks>
    /// <param name="splitRuleVersionId">The version that divided it, or null for by hand.</param>
    /// <param name="name">The expense, so the sentence names what was recorded.</param>
    Task<bool> DivisionSourceAsync(Guid transactionId, Guid? splitRuleVersionId, string name,
        CancellationToken ct = default);

    /// <summary>
    /// Points a group's expenses at the version of their rule that was in force on the day
    /// they were spent.
    /// </summary>
    /// <remarks>
    /// For a back catalogue that arrived from somewhere with no notion of versions: the
    /// migration pointed every categorised expense at its rule's only version, so an expense
    /// from 2023 claims to have been divided by a ratio agreed this year. The dates are what
    /// sorts it out, and they are already on the rows.
    /// <para>
    /// It moves no money -- not one share is read, let alone written -- so a dry run
    /// announces nothing and saves nothing, and only a real one tells the pages.
    /// </para>
    /// </remarks>
    /// <param name="dryRun">True to work out the answer and report it without saving it.</param>
    /// <returns>What it did, or would do; null when the call failed.</returns>
    Task<ReattachSummaryResponse?> ReattachAsync(Guid groupId, bool dryRun, CancellationToken ct = default);
}
