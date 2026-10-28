using Bunit;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
namespace GroupSplit.App.Web.Test.Components;
public class ExpenseSelectionStressTest : ComponentTest
{
    [Fact]
    public async Task Fifty_open_select_cancel_cycles_do_not_leak_selections_or_dialogs()
    {
        var provider=Render<MudDialogProvider>();
        for(var iteration=0;iteration<50;iteration++)
        {
            IDialogReference dialog=null!;
            await provider.InvokeAsync(async()=>dialog=await Services.GetRequiredService<IDialogService>().ShowAsync<ExpenseChoiceDialog>("Stress choice",new DialogParameters<ExpenseChoiceDialog>
            {
                { x=>x.Items,new List<ExpenseChoiceDialog.Option>{new("a","Household"),new("b","Trip")} },{x=>x.Selected,"a"}
            }));
            Assert.Equal("true",provider.FindAll(".gs-selection-option")[0].GetAttribute("aria-pressed"));
            await provider.FindAll(".gs-selection-option")[1].ClickAsync(new MouseEventArgs());
            await provider.FindAll("button").Single(x=>x.TextContent.Trim()=="Cancel").ClickAsync(new MouseEventArgs());
            Assert.True((await dialog.Result!)!.Canceled);
            provider.WaitForAssertion(()=>Assert.Empty(provider.FindComponents<ExpenseChoiceDialog>()));
        }
    }
    [Fact]
    public async Task Large_list_search_handles_unicode_and_markup_as_text()
    {
        var provider=Render<MudDialogProvider>();
        var options=Enumerable.Range(0,1000).Select(i=>new ExpenseChoiceDialog.Option(i.ToString(),"Household "+i)).ToList();
        options.Add(new("special","Mar\u00eda <script>alert(1)</script>"));
        await provider.InvokeAsync(()=>Services.GetRequiredService<IDialogService>().ShowAsync<ExpenseChoiceDialog>("Large list",new DialogParameters<ExpenseChoiceDialog>{{x=>x.Items,options},{x=>x.Selected,"999"}}));
        Assert.Equal(1001,provider.FindAll(".gs-selection-option").Count);
        await provider.Find("input").InputAsync(new ChangeEventArgs{Value="Mar\u00eda"});
        provider.WaitForAssertion(()=>Assert.Single(provider.FindAll(".gs-selection-option")));
        Assert.Empty(provider.FindAll("script"));
        await provider.Find("input").InputAsync(new ChangeEventArgs{Value="not present"});
        provider.WaitForAssertion(()=>Assert.Empty(provider.FindAll(".gs-selection-option")));
        Assert.Contains("No matching options",provider.Markup);
        await provider.Find("input").InputAsync(new ChangeEventArgs{Value=""});
        provider.WaitForAssertion(()=>Assert.Equal("true",provider.FindAll(".gs-selection-option")[999].GetAttribute("aria-pressed")));
    }
}
