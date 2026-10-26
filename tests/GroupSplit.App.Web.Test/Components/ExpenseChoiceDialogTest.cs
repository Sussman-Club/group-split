using Bunit;
using Microsoft.AspNetCore.Components;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class ExpenseChoiceDialogTest : ComponentTest
{
    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () => dialog = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ExpenseChoiceDialog>("Choose a group", new DialogParameters<ExpenseChoiceDialog>
            {
                { x => x.Items, new List<ExpenseChoiceDialog.Option>
                    { new("home", "Our household"), new("trip", "Weekend trip"), new("unavailable", "Unavailable", Disabled: true) } },
                { x => x.Selected, "home" }
            }));
        return (provider, dialog);
    }

    [Fact]
    public async Task Picking_an_option_does_not_commit_until_confirmed()
    {
        var (provider, dialog) = await OpenAsync();
        await provider.FindAll(".gs-selection-option")[1].ClickAsync(new MouseEventArgs());
        Assert.False(dialog.Result!.IsCompleted);
        await provider.FindAll("button").Single(x => x.TextContent.Trim() == "Use selection").ClickAsync(new MouseEventArgs());
        var result = await dialog.Result!;
        Assert.False(result!.Canceled);
        Assert.Equal("trip", result.Data);
    }

    [Fact]
    public async Task Cancel_discards_the_pending_selection()
    {
        var (provider, dialog) = await OpenAsync();
        await provider.FindAll(".gs-selection-option")[1].ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(x => x.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs());
        Assert.True((await dialog.Result!)!.Canceled);
    }

    [Fact]
    public async Task Searching_filters_choices_without_resetting_the_selection()
    {
        var (provider, _) = await OpenAsync();
        await provider.Find("input").InputAsync(new ChangeEventArgs { Value = "TRIP" });
        provider.WaitForAssertion(() => Assert.Single(provider.FindAll(".gs-selection-option")));
        Assert.Contains("Weekend trip", provider.Find(".gs-selection-option").TextContent);
        await provider.Find("input").InputAsync(new ChangeEventArgs { Value = "" });
        provider.WaitForAssertion(() => Assert.Equal("true", provider.Find(".gs-selection-option").GetAttribute("aria-pressed")));
        Assert.True(provider.FindAll(".gs-selection-option")[2].HasAttribute("disabled"));
    }
}
