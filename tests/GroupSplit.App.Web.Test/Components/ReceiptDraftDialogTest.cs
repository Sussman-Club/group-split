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

    private static SaveReceiptRequest Draft() => new()
    {
        Subtotal = 28m,
        Total = 28m,
        Items =
        [
            new ReceiptItemInput { Name = "Water", Description = "WATER 40 PK", UnitPrice = 10m, TotalPrice = 10m },
            new ReceiptItemInput { Name = "Pizza", Description = "LARGE CHEESE", UnitPrice = 20m, TotalPrice = 20m },
            new ReceiptItemInput { Name = "Coupon", UnitPrice = -2m, TotalPrice = -2m },
            new ReceiptItemInput { Name = "Free sample", UnitPrice = 0m, TotalPrice = 0m }
        ]
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
    public async Task Unassigned_scanned_tax_can_be_distributed_before_using_the_receipt()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        var receipt = new SaveReceiptRequest
        {
            Subtotal = 10m, Tax = .75m, Total = 10.75m,
            Items = [new ReceiptItemInput { Name = "Milk", UnitPrice = 4m, TotalPrice = 4m },
                new ReceiptItemInput { Name = "Bread", UnitPrice = 3m, TotalPrice = 3m },
                new ReceiptItemInput { Name = "Apples", UnitPrice = 3m, TotalPrice = 3m }]
        };
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", receipt) }
            }));
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Distribute tax"))
            .ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"))
            .ClickAsync(new MouseEventArgs());
        var result = await reference!.GetReturnValueAsync<SaveReceiptRequest>();
        Assert.NotNull(result);
        Assert.Equal([.30m, .23m, .22m], result.Items.Select(item => item.TaxAmount));
        Assert.Equal(result.Tax, result.Items.Sum(item => item.TaxAmount));
    }

    [Fact]
    public async Task A_new_scan_returns_to_the_expense_sheet_instead_of_saving_a_receipt()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>().ShowAsync<ReceiptDraftDialog>(
                "Check receipt",
                new DialogParameters<ReceiptDraftDialog>
                {
                    { dialog => dialog.Transcription,
                        new ReceiptTranscriptionResponse(Guid.NewGuid(), "test", Draft()) }
                }));

        Assert.Contains("Check receipt", provider.Markup, StringComparison.Ordinal);
        var useReceipt = provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Use this receipt", StringComparison.Ordinal));

        await useReceipt.ClickAsync(new MouseEventArgs());

        var result = await reference!.GetReturnValueAsync<SaveReceiptRequest>();
        Assert.NotNull(result);
        Assert.Equal(28m, result!.Total);
        Assert.Equal(-2m, result.Items[2].TotalPrice);
        Assert.Equal(0m, result.Items[3].TotalPrice);
        _receipts.Verify(receipts => receipts.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveReceiptRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Multiple_text_changes_use_one_atomic_whole_receipt_save()
    {
        var bill = Bill();
        _receipts
            .Setup(receipts => receipts.SaveAsync(_expense, It.Is<SaveReceiptRequest>(request =>
                    request.Items[0].Description == "WATER 40 PK"
                    && request.Items[1].Description == "LARGE CHEESE"),
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
    public async Task Financial_changes_use_the_whole_receipt_save()
    {
        var bill = Bill();
        _receipts
            .Setup(receipts => receipts.SaveAsync(_expense, It.Is<SaveReceiptRequest>(request =>
                    request.Items[0].TotalPrice == 11m
                    && request.Items[1].TotalPrice == 19m),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var (provider, reference) = await OpenAsync(bill);
        var totals = provider.FindComponents<MudNumericField<decimal>>()
            .Where(field => field.Instance.Label == "Line total")
            .ToList();

        await provider.InvokeAsync(() => totals[0].Instance.ValueChanged.InvokeAsync(11m));
        await provider.InvokeAsync(() => totals[1].Instance.ValueChanged.InvokeAsync(19m));
        await provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        _receipts.Verify(receipts => receipts.SaveAsync(_expense, It.Is<SaveReceiptRequest>(request =>
                request.Items[0].TotalPrice == 11m
                && request.Items[1].TotalPrice == 19m),
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

    [Fact]
    public async Task Editing_a_coupon_line_and_one_item_name_saves_both_changes()
    {
        var original = Bill();
        var coupon = Line("Old coupon", -2m, "SAVE2");
        var bill = original with { Subtotal = 28m, Total = 28m, Items = [..original.Items, coupon] };
        _receipts.Setup(receipts => receipts.SaveAsync(_expense, It.IsAny<SaveReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var (provider, _) = await OpenAsync(bill);
        var names = provider.FindComponents<MudTextField<string>>()
            .Where(field => field.Instance.Label == "Item").ToList();
        await provider.InvokeAsync(() => names[2].Instance.ValueChanged.InvokeAsync("New coupon"));
        await provider.InvokeAsync(() => names[0].Instance.ValueChanged.InvokeAsync("Bottled water"));
        await provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        _receipts.Verify(receipts => receipts.SaveAsync(_expense, It.Is<SaveReceiptRequest>(request =>
                request.Items[0].Name == "Bottled water"
                && request.Items[2].Name == "New coupon"
                && request.Items[2].TotalPrice == -2m),
            It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(receipts => receipts.PatchItemAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<ReceiptItemPatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Marking_an_item_tax_exempt_hides_and_clears_its_tax_rate()
    {
        var original = Bill();
        var bill = original with
        {
            Items = [original.Items[0] with { TaxRate = 10m, IsTaxable = true }, original.Items[1]]
        };
        _receipts.Setup(receipts => receipts.SaveAsync(_expense, It.IsAny<SaveReceiptRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bill);

        var (provider, _) = await OpenAsync(bill);
        Assert.Equal(2, provider.FindComponents<MudNumericField<decimal?>>()
            .Count(field => field.Instance.Label == "Tax rate"));

        var status = provider.FindComponents<MudSelect<bool?>>().First();
        await provider.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync(false));

        Assert.Single(provider.FindComponents<MudNumericField<decimal?>>()
            .Where(field => field.Instance.Label == "Tax rate"));

        await provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Save receipt", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        _receipts.Verify(receipts => receipts.SaveAsync(_expense, It.Is<SaveReceiptRequest>(request =>
                request.Items[0].IsTaxable == false && request.Items[0].TaxRate == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
