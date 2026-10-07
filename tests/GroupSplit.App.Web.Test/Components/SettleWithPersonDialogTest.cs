using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Settling with one person across groups, only up to a day (#247).
/// </summary>
/// <remarks>
/// Picking a day reads the cross-group plan through it, because the figure and the groups it
/// lands in both change with the period -- and the request has to carry the day and the offset
/// it was read with, or the server would spread the payment by a different plan.
/// </remarks>
public class SettleWithPersonDialogTest : ComponentTest
{
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly DateOnly EndOfAugust = new(2026, 8, 31);

    private readonly Mock<IUsersClient> _users = new();

    public SettleWithPersonDialogTest()
    {
        Services.AddSingleton(_users.Object);
    }

    private static PersonSettlement Omars(decimal home, decimal trip) => new(
        Omar, "Omar", home + trip,
        [.. new[] { new GroupDebt(Home, "Home", home), new GroupDebt(Trip, "Trip", trip) }.Where(part => part.Amount > 0)],
        false);

    private void PlanThroughAugust(PersonSettlement? omar) =>
        _users
            .Setup(client => client.GetSettlementPlanAsync(EndOfAugust, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettlementPlanResponse(omar?.Amount ?? 0, omar?.Amount ?? 0, 0, [],
                omar is null ? [] : [omar], null));

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> Open()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<SettleWithPersonDialog>("Settle", new DialogParameters<SettleWithPersonDialog>
                {
                    { d => d.Person, Omars(home: 50m, trip: 40m) },
                    { d => d.Direction, SettlementDirection.TheyPaidYou }
                }));

        return (provider, dialog);
    }

    private static async Task PickAsync(IRenderedComponent<MudDialogProvider> provider, DateOnly day)
    {
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Settle only up to a day"))
            .ClickAsync(new MouseEventArgs());

        var picker = provider.FindComponents<MudDatePicker>()
            .Single(field => field.Instance.Label == "Only activity up to");

        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(day.ToDateTime(TimeOnly.MinValue)));
    }

    private static Task ConfirmAsync(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindAll("button").Single(button => button.TextContent.Trim() == "They paid me")
            .ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Picking_a_day_settles_that_periods_figure_and_sends_the_day_with_its_offset()
    {
        PlanThroughAugust(Omars(home: 50m, trip: 0m));
        var (provider, dialog) = await Open();

        await PickAsync(provider, EndOfAugust);

        provider.WaitForAssertion(() => Assert.Contains("For activity up to 31 Aug, Omar owes you", provider.Markup));

        await ConfirmAsync(provider);

        var request = await dialog.GetReturnValueAsync<SettleWithPersonRequest>();

        Assert.Equal(50m, request!.Amount);
        Assert.Equal(EndOfAugust, request.Through);
        Assert.NotNull(request.UtcOffset);
    }

    [Fact]
    public async Task A_period_with_nothing_between_them_cannot_be_confirmed()
    {
        PlanThroughAugust(omar: null);
        var (provider, _) = await Open();

        await PickAsync(provider, EndOfAugust);

        provider.WaitForAssertion(() =>
            Assert.Contains("Nothing is outstanding between you for activity up to 31 Aug", provider.Markup));
        Assert.True(provider.FindAll("button").Single(button => button.TextContent.Trim() == "They paid me")
            .HasAttribute("disabled"));
    }
}
