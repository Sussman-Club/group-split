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
/// Re-pointing a group's past expenses at the version of their rule that was in force when
/// they were spent.
/// </summary>
/// <remarks>
/// It moves no money -- not one share is read, let alone written -- and the dry run is the
/// whole safety of it: somebody reads what would move before any of it does. So these check
/// the two halves of that bargain. Opening the dialog asks for the answer and saves nothing;
/// only the second button writes.
/// </remarks>
public class ReattachDialogTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();

    public ReattachDialogTest()
    {
        Services.AddSingleton(_transactions.Object);

        // Scoped, and MudBlazor's own dialog service left alone: the command takes one (it
        // offers to attach a bank row after an expense is created, which reattaching never
        // does), and registering a stand-in for it would take the provider's service with
        // it -- so the dialog under test would never open.
        Services.AddScoped<ITransactionCommands, TransactionCommands>();
    }

    /// <summary>
    /// The dialog opens on the answer, because the question it exists for cannot be answered
    /// without asking -- and asking costs nothing.
    /// </summary>
    [Fact]
    public async Task Opening_it_works_out_what_would_change_and_saves_nothing()
    {
        Answer(dryRun: true, changed: 812);

        var dialog = await OpenAsync();

        _transactions.Verify(client => client.ReattachTransactionsAsync(
            It.Is<ReattachTransactionsRequest>(request => request.GroupId == Flat && request.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);

        _transactions.Verify(client => client.ReattachTransactionsAsync(
            It.Is<ReattachTransactionsRequest>(request => !request.DryRun),
            It.IsAny<CancellationToken>()), Times.Never);

        // And it says so on the face of it, beside the figures.
        Assert.Contains("Dry run", dialog.Markup, StringComparison.Ordinal);

        var figures = dialog.FindAll(".gs-figure-value").Select(value => value.TextContent.Trim()).ToArray();

        Assert.Equal(["1411", "812", "63"], figures);
    }

    /// <summary>
    /// The button names the number it is about to move, so the count is read before the
    /// click rather than reported after it.
    /// </summary>
    [Fact]
    public async Task The_button_counts_what_it_would_move()
    {
        Answer(dryRun: true, changed: 812);

        var dialog = await OpenAsync();

        Assert.Contains(
            dialog.FindAll("button"),
            button => button.TextContent.Trim() == "Re-point 812 expenses");
    }

    [Fact]
    public async Task Pressing_it_asks_for_the_real_thing()
    {
        Answer(dryRun: true, changed: 812);

        var dialog = await OpenAsync();

        Answer(dryRun: false, changed: 812);

        await dialog.FindAll("button")
            .First(button => button.TextContent.Trim() == "Re-point 812 expenses")
            .ClickAsync(new MouseEventArgs());

        _transactions.Verify(client => client.ReattachTransactionsAsync(
            It.Is<ReattachTransactionsRequest>(request => request.GroupId == Flat && !request.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The figure the whole operation exists to surface: expenses older than the history
    /// written for their rule, which no version covers.
    /// </summary>
    /// <remarks>
    /// Not a failure -- an expense older than its rule's history has no honest answer -- but
    /// it is how somebody finds out the history does not reach back far enough, so it is
    /// named rather than folded into a total.
    /// </remarks>
    [Fact]
    public async Task Expenses_older_than_their_rules_history_are_named_with_the_rule()
    {
        Answer(dryRun: true, changed: 812);

        var dialog = await OpenAsync();

        var notice = dialog.Find(".gs-notice.is-warn");

        Assert.Contains("63 expenses are older", notice.QuerySelector("strong")!.TextContent,
            StringComparison.Ordinal);

        Assert.Contains("Household 3-way", notice.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A group already pointing where it should is told so, and the button that would write
    /// is not offered.
    /// </summary>
    [Fact]
    public async Task A_group_with_nothing_to_move_says_so()
    {
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReattachSummaryResponse(Flat, true, 1411, 0, 0, []));

        var dialog = await OpenAsync();

        Assert.Contains("Every expense already points where it should", dialog.Markup, StringComparison.Ordinal);

        var apply = dialog.FindAll("button").First(button => button.TextContent.Trim() == "Re-point");

        Assert.True(apply.HasAttribute("disabled"));
    }

    private void Answer(bool dryRun, int changed) =>
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReattachSummaryResponse(
                Flat, dryRun, Examined: 1411, Changed: changed, LeftWithoutAVersion: 63,
                ByRule: [new ReattachedRuleSummary(Household, "Household 3-way", 1189, changed, 63)]));

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<ReattachDialog>
        {
            { dialog => dialog.GroupId, Flat },
            { dialog => dialog.GroupName, "The flat" }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<ReattachDialog>("Re-point past expenses", parameters));

        return provider;
    }
}
