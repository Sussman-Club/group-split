using GroupSplit.App.Shared.Services.Errors;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Runs a page-state load and owns what happens when it fails. The state
/// services start their loads without awaiting them, so an exception there
/// would otherwise vanish and leave whatever was on screen before -- the
/// previous group's balances under the new group's name.
/// </summary>
public sealed class LoadGuard(ApiErrorPresenter errors)
{
    /// <param name="what">What was being loaded, to name it in the message: "your groups".</param>
    /// <returns>Whether the load completed.</returns>
    public Task<bool> RunAsync(Func<Task> load, string what) =>
        errors.TryAsync(load, $"Could not load {what}.");
}
