using MudBlazor;

namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// Reading a yes-or-no answer out of a dialog.
/// </summary>
public static class DialogExtensions
{
    /// <summary>
    /// Whether the person said yes. False for every other outcome: they said no, they
    /// cancelled, they pressed escape, or they closed it with the corner button.
    /// </summary>
    /// <remarks>
    /// This exists because the obvious way to write it throws.
    /// <para>
    /// A cancelled dialog carries no data, so its result is null.
    /// <c>GetReturnValueAsync&lt;bool&gt;</c> casts that null to <c>bool</c>, which is an
    /// unboxing conversion and raises <see cref="NullReferenceException"/>; MudBlazor guards
    /// the cast with a <see cref="InvalidCastException"/> handler, which does not catch it.
    /// So the exception escapes into whoever opened the dialog, and pressing Cancel on a
    /// confirmation shows an error instead of doing nothing.
    /// </para>
    /// <para>
    /// Asking for a <c>bool?</c> avoids the unboxing entirely: null casts to a nullable
    /// value type without complaint. Every confirmation goes through here so that the way
    /// that reads correctly is also the only way it is written.
    /// </para>
    /// </remarks>
    public static async Task<bool> ConfirmedAsync(this IDialogReference dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        return await dialog.GetReturnValueAsync<bool?>() is true;
    }
}
