using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="ICategoryCommands"/>
/// <remarks>
/// Every write announces, and the announcement is the transactions one: a category's name
/// is printed against every expense filed under it, on the expenses page, on the group page
/// and in the group's activity list. Renaming one used to leave all three showing the old
/// label until the page was reloaded, which is the defect these were written for.
/// <para>
/// Creating and deleting announce too, though neither can move a figure -- nothing is filed
/// under a category that does not exist yet, and the API refuses to delete one that still
/// has expenses. They announce because a write announces; a caller picking out which writes
/// matter to somebody else's page is how the stale name got there.
/// </para>
/// </remarks>
public sealed class CategoryCommands(
    ICategoriesClient categories,
    ApiErrorPresenter errors,
    ISnackbar snackbar,
    DataChangeNotifier changes) : ICategoryCommands
{
    public async Task<IReadOnlyList<CategoryResponse>?> ForGroupAsync(Guid groupId,
        bool includeArchived = false, CancellationToken ct = default)
    {
        IReadOnlyList<CategoryResponse> loaded = [];

        var done = await errors.TryAsync(async () =>
        {
            loaded = [.. await categories.GetCategoriesAsync(groupId, includeArchived, ct)];
        }, "Could not load the categories.");

        return done ? loaded : null;
    }

    public async Task<CategoryResponse?> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default)
    {
        CategoryResponse? created = null;

        var done = await errors.TryAsync(async () =>
        {
            created = await categories.CreateCategoryAsync(request, ct);
            snackbar.Add($"{created.Name} created.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not create the category.");

        return done ? created : null;
    }

    public async Task<CategoryResponse?> UpdateAsync(Guid categoryId, UpdateCategoryRequest request,
        CancellationToken ct = default)
    {
        CategoryResponse? updated = null;

        var done = await errors.TryAsync(async () =>
        {
            updated = await categories.UpdateCategoryAsync(categoryId, request, ct);

            // Says the part somebody would otherwise have to try in order to find out,
            // since changing how a category divides looks like it could reach backwards.
            snackbar.Add($"{updated.Name} updated. Expenses already recorded keep their split.",
                Severity.Success);

            await changes.NotifyTransactionsChangedAsync();
        }, "Could not update the category.");

        return done ? updated : null;
    }

    public async Task<CategoryResponse?> SetArchivedAsync(Guid categoryId, bool archived,
        CancellationToken ct = default)
    {
        CategoryResponse? saved = null;

        var done = await errors.TryAsync(async () =>
        {
            saved = archived
                ? await categories.ArchiveCategoryAsync(categoryId, ct)
                : await categories.UnarchiveCategoryAsync(categoryId, ct);

            // Says the half that is not obvious. "Groceries archived" on its own reads like
            // a year of shopping went with it, which is the fear that makes people not press
            // the button -- and then keep a category they have stopped using for ever.
            snackbar.Add(
                archived
                    ? $"{saved.Name} archived. Expenses filed under it keep it."
                    : $"{saved.Name} is back.",
                Severity.Success);

            await changes.NotifyTransactionsChangedAsync();
        }, archived ? "Could not archive the category." : "Could not bring the category back.");

        return done ? saved : null;
    }

    public Task<bool> DeleteAsync(Guid categoryId, string name, CancellationToken ct = default) =>
        errors.TryAsync(async () =>
        {
            await categories.DeleteCategoryAsync(categoryId, ct);
            snackbar.Add($"{name} deleted.", Severity.Success);
            await changes.NotifyTransactionsChangedAsync();
        }, "Could not delete the category.");
}
