using Bunit;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class ExpenseReviewDialogTest : ComponentTest
{
    [Fact]
    public async Task No_changes_disables_confirmation_and_excludes_unchanged_rows()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>()
            .ShowAsync<ExpenseReviewDialog>("Review changes", new DialogParameters<ExpenseReviewDialog>
            {
                { x => x.Changes, new List<ExpenseReviewDialog.Change> { new("Category", "No category", "Groceries", false) } },
                { x => x.Shares, new List<ExpenseReviewDialog.Share> { new("You", 40m, 40m) } }
            }));
        Assert.Empty(provider.FindAll(".gs-review-change"));
        Assert.Empty(provider.FindAll(".gs-review-share"));
        Assert.Contains("No changes to save", provider.Markup);
        Assert.True(provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm changes").HasAttribute("disabled"));
    }

    [Fact]
    public void Identical_shares_in_a_different_order_are_not_sent_again()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var original = new GroupSplit.Shared.UpdateTransactionRequest
        {
            Name = "Dinner", Amount = 20m,
            Splits = [new() { UserId = first, Amount = 10m }, new() { UserId = second, Amount = 10m }]
        };
        var updated = new GroupSplit.Shared.UpdateTransactionRequest
        {
            Name = "Dinner corrected", Amount = 20m,
            Splits = original.Splits.Reverse().ToArray()
        };
        var method = typeof(UpdateTransactionDialog).GetMethod("CreatePatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var patch = (Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<GroupSplit.Shared.UpdateTransactionRequest>)method.Invoke(null, [original, updated, false])!;
        var operation = Assert.Single(patch.Operations);
        Assert.Equal("/Name", operation.path, ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_shows_old_and_new_shares_and_returns_only_the_explicit_decision(bool confirm)
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () => dialog = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ExpenseReviewDialog>("Review changes", new DialogParameters<ExpenseReviewDialog>
            {
                { x => x.Changes, new List<ExpenseReviewDialog.Change> { new("Name", "Groceries", "Groceries"), new("Amount", "$80.00", "$100.00") } },
                { x => x.Shares, new List<ExpenseReviewDialog.Share> { new("You", 40m, 50m), new("Daniel", 40m, 50m) } }
            }));
        Assert.False(dialog.Result!.IsCompleted);
        Assert.Single(provider.FindAll(".gs-review-change"));
        Assert.Equal(2, provider.FindAll(".gs-review-share").Count);
        Assert.Contains("$40.00", provider.Find(".gs-review-share").TextContent);
        Assert.Contains("$50.00", provider.Find(".gs-review-share").TextContent);
        await provider.FindAll("button").Single(x => x.TextContent.Trim() == (confirm ? "Confirm changes" : "Keep editing")).ClickAsync(new MouseEventArgs());
        Assert.Equal(!confirm, (await dialog.Result!)!.Canceled);
    }
}
