namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Where a write announces itself. The page states each keep their own copy of what the
/// server holds -- the expenses page has every expense the person paid, the groups page
/// has the selected group's expenses and balances -- and a write through one of them, or
/// through a dialog that talks to the API on its own, used to leave the others showing
/// the old figures until the page was reloaded. Every write now raises the matching event
/// here instead, and each state service re-reads what it caches, so what is on screen no
/// longer depends on which page the change was made from.
///
/// Handlers are awaited: by the time a notify call returns, every state has caught up.
/// A handler that fails is its own concern -- the state services run theirs under the
/// <see cref="LoadGuard"/>, which shows the failure and does not throw.
/// </summary>
public sealed class DataChangeNotifier
{
    /// <summary>An expense was created, edited, deleted or settled.</summary>
    public event Func<Task>? TransactionsChanged;

    /// <summary>A group was created or renamed, or a member joined or left it.</summary>
    public event Func<Task>? GroupsChanged;

    public Task NotifyTransactionsChangedAsync() => RaiseAsync(TransactionsChanged);

    public Task NotifyGroupsChangedAsync() => RaiseAsync(GroupsChanged);

    private static Task RaiseAsync(Func<Task>? handlers) =>
        handlers is null
            ? Task.CompletedTask
            : Task.WhenAll(handlers.GetInvocationList().Cast<Func<Task>>().Select(handler => handler()));
}
