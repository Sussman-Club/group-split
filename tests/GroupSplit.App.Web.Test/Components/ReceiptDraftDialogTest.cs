using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptDraftDialogTest : ComponentTest
{
    private readonly Mock<IReceiptCommands> _receipts = new();
    private readonly Guid _expense = Guid.NewGuid();

    public ReceiptDraftDialogTest()
    {
        Services.AddSingleton(_receipts.Object);
    }

    private ReceiptResponse Bill() => new(
        Guid.NewGuid(), _expense, 30m, 0m, 0m, 30m, 0, false, false,
        [
            Line("Water", 10m, "WATER 40 PK"),
            Line("Pizza", 20m, "LARGE CHEESE")
        ]) { CanEdit = true };

    private static ReceiptItemResponse Line(string name, decimal price, string description) =>
        new(Guid.NewGuid(), name, price, 1m, price, 0m, null, null, null)
        {
            Description = description
        };

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenAsync(
        ReceiptResponse bill)
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>().ShowAsync<ReceiptDraftDialog>(
                "Edit itemized receipt",
                new DialogParameters<ReceiptDraftDialog>
                {
                    { dialog => dialog.TransactionId, _expense },
                    { dialog => dialog.ExpenseAmount, bill.Total },
                    { dialog => dialog.Receipt, bill }
                }));

        return (provider, reference!);
    }

    [Fact]
    public async Task Multiple_text_changes_use_one_atomic_whole_receipt_save()
    {
        var bill = Bill();
        _receipts
            .Setup(receipts => receipts.SaveAsync(_expense, It.IsAny<SaveReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var (provider, reference) = await OpenAsync(bill);
        var names = provider.FindComponents<MudTextField<string>>()
            .Where(field => field.Instance.Label == "Item")
            .ToList();

        await provider.InvokeAsync(() => names[0].Instance.ValueChanged.InvokeAsync("Bottled water"));
        await provider.InvokeAsync(() => names[1].Instance.ValueChanged.InvokeAsync("Cheese pizza"));
        await provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        _receipts.Verify(receipts => receipts.SaveAsync(_expense, It.IsAny<SaveReceiptRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(receipts => receipts.PatchItemAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<ReceiptItemPatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(reference.Result.IsCompleted);
    }

    [Fact]
    public async Task A_single_text_change_uses_the_item_patch()
    {
        var bill = Bill();
        _receipts
            .Setup(receipts => receipts.PatchItemAsync(_expense, bill.Items[0].Id,
                It.IsAny<Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<ReceiptItemPatch>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var (provider, reference) = await OpenAsync(bill);
        var name = provider.FindComponents<MudTextField<string>>()
            .First(field => field.Instance.Label == "Item");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Bottled water"));
        await provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        _receipts.Verify(receipts => receipts.PatchItemAsync(_expense, bill.Items[0].Id,
            It.IsAny<Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<ReceiptItemPatch>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(receipts => receipts.SaveAsync(It.IsAny<Guid>(),
            It.IsAny<SaveReceiptRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(reference.Result.IsCompleted);
    }
}
