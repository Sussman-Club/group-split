using GroupSplit.App.Shared.Extensions;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Dialogs;

/// <summary>
/// Reading a yes-or-no answer back out of a confirmation dialog. Against the real
/// MudBlazor reference rather than a stand-in, because the bug being pinned here is in
/// MudBlazor: a hand-rolled fake would have been written to behave, and so would have
/// agreed with the broken code.
/// </summary>
/// <remarks>
/// Cancelling a delete confirmation used to surface an error to the person who had just
/// declined to delete anything -- the one outcome that should have been silent.
/// </remarks>
public class ConfirmationResultTest
{
    /// <summary>
    /// A reference whose dialog has already finished with <paramref name="result"/>.
    /// <c>Dismiss</c> is what a dialog's own Cancel and Close call, so this is the state
    /// the component is really handed.
    /// </summary>
    private static IDialogReference Closed(DialogResult result)
    {
        var reference = new DialogReference(Guid.NewGuid(), Mock.Of<IDialogService>());

        reference.Dismiss(result);

        return reference;
    }

    [Fact]
    public async Task Saying_yes_confirms()
    {
        Assert.True(await Closed(DialogResult.Ok(true)).ConfirmedAsync());
    }

    [Fact]
    public async Task Saying_no_does_not_confirm()
    {
        Assert.False(await Closed(DialogResult.Ok(false)).ConfirmedAsync());
    }

    /// <summary>The reported bug: this threw instead of answering.</summary>
    [Fact]
    public async Task Cancelling_does_not_confirm()
    {
        Assert.False(await Closed(DialogResult.Cancel()).ConfirmedAsync());
    }

    /// <summary>
    /// Why the extension exists at all, kept executable so it stays honest. Asking
    /// MudBlazor for a non-nullable <c>bool</c> unboxes the cancelled dialog's null
    /// result; its <see cref="InvalidCastException"/> guard does not cover that, so the
    /// throw reaches the caller.
    /// <para>
    /// If MudBlazor fixes this, this test fails -- and that failure is the signal to
    /// delete the extension, not to change this line.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Asking_for_a_plain_bool_is_what_threw()
    {
        var dialog = Closed(DialogResult.Cancel());

        await Assert.ThrowsAsync<NullReferenceException>(
            async () => await dialog.GetReturnValueAsync<bool>());
    }
}
