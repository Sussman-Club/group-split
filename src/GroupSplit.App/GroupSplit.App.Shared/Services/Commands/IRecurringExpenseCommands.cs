using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// Expenses that repeat on a schedule (#546): the group's list, and every write to one. See
/// <see cref="IGroupCommands"/> for why these exist at all.
/// </summary>
/// <remarks>
/// Each method shows its own failure and returns null (or false), so a caller only has to
/// decide what to do when it worked.
/// </remarks>
public interface IRecurringExpenseCommands
{
    Task<IReadOnlyList<RecurringExpenseResponse>?> ForGroupAsync(Guid groupId, CancellationToken ct = default);

    Task<RecurringExpenseResponse?> UpdateAsync(Guid id, RecurringExpenseRequest request, CancellationToken ct = default);

    Task<RecurringExpenseResponse?> SetPausedAsync(Guid id, bool paused, CancellationToken ct = default);

    Task<bool> StopAsync(Guid id, CancellationToken ct = default);
}
