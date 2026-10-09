using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Recording a repayment inside a group, and settling only up to a day (#247).
/// </summary>
/// <remarks>
/// Picking a day reads the group's balances through it rather than working the figure out:
/// through a day the API counts the repayments made for it, which is the half a client
/// adding up its own expenses would leave out.
/// </remarks>
public class SettleDialogTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly DateOnly EndOfAugust = new(2026, 8, 31);

    private readonly Mock<IGroupsClient> _groups = new();

    public SettleDialogTest()
    {
        Services.AddSingleton(_groups.Object);
    }

    private void OwedThroughAugust(decimal amount) =>
        _groups
            .Setup(client => client.GetGroupUserBalanceAsync(Flat, EndOfAugust, It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserGroupBalanceResponse
            {
                OwedToYou = amount > 0 ? [new DebtInfo { UserId = Omar, UserName = "Omar", Amount = amount }] : []
            });

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> Open()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<SettleDialog>("Settle", new DialogParameters<SettleDialog>
                {
                    { d => d.GroupId, Flat },
                    { d => d.UserId, Omar },
                    { d => d.Username, "Omar" },
                    { d => d.TotalDebt, 70m },
                    { d => d.Direction, SettlementDirection.TheyPaidYou }
                }));

        return (provider, dialog);
    }

    private static async Task PickAsync(IRenderedComponent<MudDialogProvider> provider, DateOnly? day)
    {
        if (!provider.FindAll("button").Any(button => button.TextContent.Contains("Settle only up to a day")))
            return;

        await provider.FindAll("button").Single(button => button.TextContent.Contains("Settle only up to a day"))
            .ClickAsync(new MouseEventArgs());

        var picker = provider.FindComponents<MudDatePicker>()
            .Single(field => field.Instance.Label == "Only activity up to");

        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(day?.ToDateTime(TimeOnly.MinValue)));
    }

    [Fact]
    public async Task Without_a_day_it_settles_the_whole_of_it_and_reads_nothing()
    {
        var (provider, dialog) = await Open();

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        var request = await dialog.GetReturnValueAsync<SettleRequest>();

        Assert.Equal(70m, request!.Amount);
        Assert.Null(request.Through);
        _groups.Verify(client => client.GetGroupUserBalanceAsync(It.IsAny<Guid>(), It.IsAny<DateOnly?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Picking_a_day_settles_what_that_period_left_owing_and_remembers_the_day()
    {
        OwedThroughAugust(50m);
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);

        provider.WaitForAssertion(() => Assert.Contains("For activity up to 31 Aug, Omar owes you", provider.Markup));

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        var request = await dialog.GetReturnValueAsync<SettleRequest>();

        Assert.Equal(50m, request!.Amount);
        Assert.Equal(EndOfAugust, request.Through);
    }

    private static Task TypeAsync(IRenderedComponent<MudDialogProvider> provider, decimal amount)
    {
        var field = provider.FindComponents<MudNumericField<decimal>>()
            .Single(candidate => candidate.Instance.Label == "Amount they paid");

        return provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(amount));
    }

    [Fact]
    public async Task More_than_the_period_left_owing_is_recorded_as_typed()
    {
        // The figure is a suggestion. The money that moved is what gets written.
        OwedThroughAugust(50m);
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);
        provider.WaitForAssertion(() => Assert.Contains("For activity up to 31 Aug", provider.Markup));

        await TypeAsync(provider, 60.80m);
        provider.WaitForAssertion(() => Assert.Contains("more than that period left owing", provider.Markup));

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        var request = await dialog.GetReturnValueAsync<SettleRequest>();

        Assert.Equal(60.80m, request!.Amount);
        Assert.Equal(EndOfAugust, request.Through);
    }

    [Fact]
    public async Task A_period_with_nothing_left_owing_still_records_what_was_typed()
    {
        OwedThroughAugust(0m);
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);

        provider.WaitForAssertion(() =>
            Assert.Contains("Nothing is outstanding between you for activity up to 31 Aug", provider.Markup));

        await TypeAsync(provider, 12.50m);
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        var request = await dialog.GetReturnValueAsync<SettleRequest>();

        Assert.Equal(12.50m, request!.Amount);
        Assert.Equal(EndOfAugust, request.Through);
    }

    [Fact]
    public async Task Nothing_is_not_an_amount()
    {
        OwedThroughAugust(0m);
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);
        provider.WaitForAssertion(() => Assert.Contains("Nothing is outstanding", provider.Markup));

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        provider.WaitForAssertion(() => Assert.Contains("Amount must be greater than 0.", provider.Markup));
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task Clearing_the_day_goes_back_to_the_whole_of_it()
    {
        OwedThroughAugust(50m);
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);
        provider.WaitForAssertion(() => Assert.Contains("For activity up to 31 Aug", provider.Markup));

        var picker = provider.FindComponents<MudDatePicker>()
            .Single(field => field.Instance.Label == "Only activity up to");
        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(null));

        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm")
            .ClickAsync(new MouseEventArgs());

        var request = await dialog.GetReturnValueAsync<SettleRequest>();

        Assert.Equal(70m, request!.Amount);
        Assert.Null(request.Through);
    }
}
