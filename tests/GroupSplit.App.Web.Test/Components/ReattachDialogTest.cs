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
    /// "No version fits" counts the expenses whose rule's history is too short, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// It used to print `LeftWithoutAVersion`, which also counts every expense with no
    /// category or a category naming no rule -- 222 of them here. Those end with no version
    /// because there is nothing for them to point at, which is ordinary. So a group with
    /// uncategorised spending and a complete rule history showed an alarming clay figure over
    /// a table that itself said those expenses had no category: two numbers on one screen
    /// contradicting each other, with the frightening one on top.
    /// </remarks>
    [Fact]
    public async Task The_unfitted_figure_counts_only_what_a_rules_history_does_not_reach()
    {
        Answer(dryRun: true, changed: 812);

        var dialog = await OpenAsync();

        var unfitted = dialog.FindAll(".gs-figure-value")[2].TextContent.Trim();

        Assert.Equal("63", unfitted);
        Assert.NotEqual("285", unfitted);

        // The row for those 222 is still drawn, because they are in the Examined total and a
        // table that did not account for them would look wrong.
        var unruled = dialog.FindAll(".gs-counts-row.is-quiet").Single();

        Assert.Contains("222", unruled.TextContent, StringComparison.Ordinal);
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
            .ReturnsAsync(new ReattachSummaryResponse(Flat, true, 1411, 0, 0,
                [new ReattachedRuleSummary(Household, "Household 3-way", 1411, 0, 0)]));

        var dialog = await OpenAsync();

        Assert.Contains("Every expense already points where it should", dialog.Markup, StringComparison.Ordinal);

        var apply = dialog.FindAll("button").First(button => button.TextContent.Trim() == "Re-point");

        Assert.True(apply.HasAttribute("disabled"));
    }

    /// <summary>
    /// A summary the service could actually produce.
    /// </summary>
    /// <remarks>
    /// The figures have to hang together or the test proves nothing about a screen that
    /// derives most of what it shows. 1,411 expenses, of which 1,189 are filed under a
    /// category with a rule and 222 are not; `LeftWithoutAVersion` counts every expense that
    /// ends up pointing at no version, which is those 222 plus the 63 the rule's history does
    /// not reach back to. This fixture said 63, which no run of the service could return --
    /// and it was built that way because the dialog was reading that field as though it meant
    /// only the second group.
    /// </remarks>
    private void Answer(bool dryRun, int changed) =>
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReattachSummaryResponse(
                Flat, dryRun, Examined: 1411, Changed: changed, LeftWithoutAVersion: 285,
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
