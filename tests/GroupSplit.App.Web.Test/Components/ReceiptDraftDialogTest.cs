using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
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
    public async Task Changing_numbers_offers_manual_tax_recalculation_for_taxable_items()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference reference = null!;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", new SaveReceiptRequest
                {
                    Subtotal = 15m, Tax = 1m, Total = 16m,
                    Items = [new() { Name = "A", UnitPrice = 4m, TotalPrice = 4m, IsTaxable = true },
                        new() { Name = "B", UnitPrice = 8m, TotalPrice = 8m, IsTaxable = true },
                        new() { Name = "Exempt", UnitPrice = 3m, TotalPrice = 3m, IsTaxable = false }]
                }) }
            }));
        Assert.DoesNotContain("Spread tax across items", provider.Markup);
        var tax = provider.FindComponents<DecimalField<decimal>>().Single(field => field.Instance.Label == "Tax");
        var total = provider.FindComponents<DecimalField<decimal>>().Single(field => field.Instance.Label == "Total paid");
        await provider.InvokeAsync(() => tax.Instance.ValueChanged.InvokeAsync(.97m));
        await provider.InvokeAsync(() => total.Instance.ValueChanged.InvokeAsync(15.97m));
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Spread tax across items"))
            .ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"))
            .ClickAsync(new MouseEventArgs());
        var result = await reference.GetReturnValueAsync<SaveReceiptRequest>();
        Assert.Equal(.32m, result!.Items[0].TaxAmount);
        Assert.Equal(.65m, result.Items[1].TaxAmount);
        Assert.Equal(0m, result.Items[2].TaxAmount);
    }

    [Fact]
    public async Task Receipt_with_only_total_tax_can_be_accepted_without_a_tax_decision()
    {
        var draft = new SaveReceiptRequest
        {
            Subtotal = 28m, Tax = .96m, Total = 28.96m, Items = Draft().Items.Select(item => new ReceiptItemInput
            { Name = item.Name, UnitPrice = item.UnitPrice, TotalPrice = item.TotalPrice, IsTaxable = item.TotalPrice > 0 ? true : null }).ToArray()
        };
        var provider = Render<MudDialogProvider>();
        IDialogReference reference = null!;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", draft) }
            }));
        var accept = () => provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"));
        Assert.False(accept().HasAttribute("disabled"));
        await accept().ClickAsync(new MouseEventArgs());
        var result = await reference.GetReturnValueAsync<SaveReceiptRequest>();
        Assert.Equal(.96m, result!.Tax);
        Assert.DoesNotContain("Spread tax across items", provider.Markup);
        Assert.All(result.Items.Where(item => item.TotalPrice > 0), item => Assert.True(item.IsTaxable));
        Assert.All(result.Items.Where(item => item.TotalPrice <= 0), item => Assert.Null(item.IsTaxable));
    }

    [Fact]
    public async Task Rates_with_missing_line_taxes_show_calculated_values_without_an_error_or_manual_distribution()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        var receipt = new SaveReceiptRequest
        {
            Subtotal = 10m, Tax = .75m, Total = 10.75m,
            Items = [new ReceiptItemInput { Name = "Milk", UnitPrice = 4m, TotalPrice = 4m, TaxRate = 7.5m },
                new ReceiptItemInput { Name = "Bread", UnitPrice = 3m, TotalPrice = 3m, TaxRate = 7.5m },
                new ReceiptItemInput { Name = "Apples", UnitPrice = 3m, TotalPrice = 3m, TaxRate = 7.5m }]
        };
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", receipt) }
            }));
        var taxes = provider.FindComponents<MudNumericField<decimal>>()
            .Where(field => field.Instance.Label == "Line tax").ToArray();
        Assert.Equal(["0.30", "0.22", "0.23"], taxes.Select(field => field.Find("input").GetAttribute("value")));
        Assert.DoesNotContain("Line tax must add up", provider.Markup);
        Assert.DoesNotContain(provider.FindAll("button"), button => button.TextContent.Contains("Distribute tax"));
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"))
            .ClickAsync(new MouseEventArgs());
        var result = await reference!.GetReturnValueAsync<SaveReceiptRequest>();
        Assert.NotNull(result);
        Assert.Equal(.75m, result.Items.Sum(item => item.TaxAmount));
    }

    [Theory]
    [InlineData("Quantity", 0, "Quantities must be positive")]
    [InlineData("Quantity", -1, "Quantities must be positive")]
    [InlineData("Quantity", 1.0001, "Quantities must be positive")]
    [InlineData("Tax rate", -1, "Tax rates must be between")]
    [InlineData("Tax rate", 101, "Tax rates must be between")]
    public async Task Invalid_numeric_edits_stay_in_review_instead_of_closing_the_draft(
        string label, decimal value, string message)
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", Draft()) }
            }));
        if (label == "Tax rate")
        {
            var field = provider.FindComponents<MudNumericField<decimal?>>()
                .First(field => field.Instance.Label == label);
            await provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
        }
        else
        {
            var field = provider.FindComponents<MudNumericField<decimal>>()
                .First(field => field.Instance.Label == label);
            await provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
        }
        Assert.Contains(message, provider.Markup);
        var save = provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"));
        Assert.True(save.HasAttribute("disabled"));
        Assert.False(reference!.Result!.IsCompleted);
    }

    [Fact]
    public async Task An_unparseable_amount_cannot_silently_save_the_previous_value()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<ReceiptDraftDialog>("Check receipt", new DialogParameters<ReceiptDraftDialog>
            {
                { component => component.Transcription, new ReceiptTranscriptionResponse(Guid.Empty, "test", Draft()) }
            }));
        var field = provider.FindComponents<MudNumericField<decimal>>()
            .First(field => field.Instance.Label == "Line total");
        await field.Find("input").ChangeAsync(new ChangeEventArgs { Value = "1.234,56" });
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"))
            .ClickAsync(new MouseEventArgs());
        Assert.False(reference!.Result!.IsCompleted);
        Assert.Contains("Correct the highlighted", provider.Markup);
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
