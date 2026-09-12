using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Every write to a category, and the one read that belongs with them. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
/// <remarks>
/// A category is half of what a group means by "how Groceries is split" -- the label. The
/// other half is a <see cref="ISplitRuleCommands">split rule</see>, and the dialog that
/// edits them together does two writes, one through each of these.
/// <para>
/// The category is the half that speaks: a person named one thing and pressed one button,
/// so one message says what happened to it, and the rule beside it stays quiet.
/// </para>
/// </remarks>
public interface ICategoryCommands
{
    /// <summary>
    /// A group's categories, each carrying the name of the rule it divides by.
    /// </summary>
    /// <remarks>
    /// A read, and it lives here because the dialog that manages categories is the only
    /// thing that reads them by way of the command layer, and it reads them to write to
    /// them. Unlike <see cref="ITransactionCommands.PreviewAsync"/> a failure is shown:
    /// this is asked once, when the dialog opens, and a dialog that opens empty for no
    /// stated reason is worse than one that says why.
    /// </remarks>
    /// <returns>Null when the read failed, which the caller can take as "do not open".</returns>
    /// <param name="includeArchived">
    /// True for the screen that manages categories, which has to show what the group retired
    /// in order to offer bringing it back. False -- the default -- is what every picker
    /// wants: the labels the group files under today.
    /// </param>
    Task<IReadOnlyList<CategoryResponse>?> ForGroupAsync(Guid groupId, bool includeArchived = false,
        CancellationToken ct = default);

    Task<CategoryResponse?> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Renames a category, or points it at a different division. Neither touches what is
    /// already recorded -- an expense holds the amounts it was divided into.
    /// </summary>
    Task<CategoryResponse?> UpdateAsync(Guid categoryId, UpdateCategoryRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a category. The API refuses one that still has expenses filed under it,
    /// rather than taking a group's spending history with it, so this is a refusal the
    /// person may well see.
    /// </summary>
    Task<bool> DeleteAsync(Guid categoryId, string name, CancellationToken ct = default);

    /// <summary>
    /// Retires a category, or brings it back.
    /// </summary>
    /// <remarks>
    /// The answer to what people mean by deleting one they have been using: it leaves the
    /// pickers and the lists, and every expense filed under it goes on naming it. Deleting
    /// is refused outright once anything is filed under it, so for most categories older
    /// than a week this is the only way to stop being offered them.
    /// </remarks>
    Task<CategoryResponse?> SetArchivedAsync(Guid categoryId, bool archived,
        CancellationToken ct = default);
}
