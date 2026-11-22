using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// A group's repeating expenses in its settings (#546): what comes round next, why one is
/// held back, and pausing, changing and stopping one.
/// </summary>
public class GroupRecurringExpensesTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Rent = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid();

    private readonly Mock<IRecurring_expensesClient> _recurring = new();

    public GroupRecurringExpensesTest()
    {
        Services.AddSingleton(_recurring.Object);
        Services.AddSingleton(Mock.Of<IGroupsClient>());
        Services.AddScoped<IRecurringExpenseCommands, RecurringExpenseCommands>();
    }

    [Fact]
    public async Task Each_says_how_much_how_often_who_pays_and_when_next()
    {
        Listing(ARent());

        var (section, _) = await RenderAsync();

        var row = section.Find("[data-testid=recurring-expense]").TextContent;
        Assert.Contains("Rent", row, StringComparison.Ordinal);
        Assert.Contains("$900.00", row, StringComparison.Ordinal);
        Assert.Contains("Every month", row, StringComparison.Ordinal);
        Assert.Contains("Paid by Ana", row, StringComparison.Ordinal);
        Assert.Contains("Next on 1 Dec 2026", row, StringComparison.Ordinal);
        Assert.Empty(section.FindAll("[data-testid=recurring-problem]"));
    }

    [Fact]
    public async Task One_held_back_says_why()
    {
        Listing(ARent() with { Problem = "The paying user is neither a member of the group nor invited to it." });

        var (section, _) = await RenderAsync();

        Assert.Contains("The one on 1 Dec 2026 could not be added: The paying user is neither",
            section.Find("[data-testid=recurring-problem]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pausing_marks_it_paused()
    {
        Listing(ARent());
        _recurring.Setup(client => client.PauseRecurringExpenseAsync(Rent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARent() with { Paused = true });

        var (section, _) = await RenderAsync();
        await Action(section, "Pause Rent").ClickAsync(new MouseEventArgs());

        section.WaitForAssertion(() =>
            Assert.Contains("Paused", section.Find("[data-testid=recurring-expense]").TextContent, StringComparison.Ordinal));
        Assert.NotNull(Action(section, "Resume Rent"));
    }

    [Fact]
    public async Task Stopping_after_confirming_takes_it_off_the_list()
    {
        Listing(ARent());

        var (section, dialogs) = await RenderAsync();
        await Action(section, "Stop Rent").ClickAsync(new MouseEventArgs());
        await dialogs.FindAll("button").Last(button => button.TextContent.Trim() == "Stop").ClickAsync(new MouseEventArgs());

        section.WaitForAssertion(() => Assert.Empty(section.FindAll("[data-testid=recurring-expense]")));
        _recurring.Verify(client => client.StopRecurringExpenseAsync(Rent, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RecurringExpenseResponse ARent() => new(
        Rent,
        Flat,
        new RecurringExpenseRequest
        {
            GroupId = Flat,
            PaidByUserId = Ana,
            Name = "Rent",
            Amount = 900,
            Frequency = RepeatFrequency.Monthly,
            StartsOn = new DateOnly(2026, 9, 1)
        },
        "Ana",
        Ana,
        new DateOnly(2026, 12, 1),
        false,
        null);

    private void Listing(params RecurringExpenseResponse[] items) =>
        _recurring.Setup(client => client.GetRecurringExpensesAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static IElement Action(IRenderedComponent<GroupRecurringExpenses> section, string label) =>
        section.FindAll("button").First(button => button.GetAttribute("aria-label") == label);

    private async Task<(IRenderedComponent<GroupRecurringExpenses> Section, IRenderedComponent<MudDialogProvider> Dialogs)>
        RenderAsync()
    {
        var provider = Render<MudDialogProvider>();
        var section = Render<GroupRecurringExpenses>(parameters => parameters
            .Add(component => component.Group, new GroupResponse(Flat, "The flat", 2)));
        await section.InvokeAsync(() => Task.CompletedTask);
        return (section, provider);
    }
}
