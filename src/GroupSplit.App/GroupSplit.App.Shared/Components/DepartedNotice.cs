using GroupSplit.App.Shared.Extensions;
using MudBlazor;

namespace GroupSplit.App.Shared.Components;

/// <summary>
/// The notice shown instead of a delete confirmation when somebody on the entry has left
/// the group. One place for it, because both lists that offer a delete -- a group's activity
/// and the activity across every group -- have to say the same thing the same way.
/// </summary>
public static class DepartedNotice
{
    public static Task ShowDepartedNoticeAsync(this IDialogService dialogs, string leaver, string noun)
    {
        var parameters = new DialogParameters<ConfirmationDialog>
        {
            { x => x.ContentText, ParticipantCopy.CannotDelete(leaver, noun) },
            { x => x.ButtonText, "OK" },
            { x => x.ShowCancel, false }
        };

        var options = new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };

        return dialogs.ShowAsync<ConfirmationDialog>($"This {noun} stays", parameters, options);
    }
}
