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
/// Every division a rule has stood for, and when.
/// </summary>
/// <remarks>
/// This replaces a collapsed panel in the edit dialog headed "What it used to be (3)", whose
/// entries read "By shares, between 3" over a date. A rule's history is the record every
/// recorded expense points at, so what it has to answer is who was on what, what that came
/// to, and what a given edit moved.
/// </remarks>
public class RuleHistoryDialogTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private static readonly Guid Current = Guid.NewGuid();
    private static readonly Guid Previous = Guid.NewGuid();

    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Lu = Guid.NewGuid();

    private static readonly DateTimeOffset Opened = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Closed = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IGroupsClient> _groups = new();

    public RuleHistoryDialogTest()
    {
        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, Flat, "Household 3-way",
            [
                // Newest first, as the API returns them.
                new SplitRuleVersionResponse(Current, Closed, null, new EvenSplitRuleDto([Ana, Lu])),

                new SplitRuleVersionResponse(Previous, Opened, Closed,
                    new SharesSplitRuleDto { Shares = { [Ana] = 3, [Lu] = 1 } })
            ]));

        SplitRules
            .Setup(client => client.UpdateSplitRuleAsync(Household, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateSplitRuleRequest request, CancellationToken _) =>
                new SplitRuleDetailsResponse
                {
                    Id = id,
                    GroupId = Flat,
                    Name = request.Name,
                    VersionId = Guid.NewGuid(),
                    ChangedAt = DateTimeOffset.UtcNow,
                    Definition = request.Definition
                });

        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new CategoryResponse(Guid.NewGuid(), Flat, "Groceries", Household, "Household 3-way"),
                new CategoryResponse(Guid.NewGuid(), Flat, "Utilities", Household, "Household 3-way")
            ]);

        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new UserInfo(Ana, "Ana", "Benitez", null),
                new UserInfo(Lu, "Lu", "Ferrer", null)
            ]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton<ISplitRuleCommands, SplitRuleCommands>();
    }

    /// <summary>
    /// One version per division, with the one the rule says now marked as such.
    /// </summary>
    [Fact]
    public async Task Every_division_the_rule_has_stood_for_is_listed_with_the_current_one_marked()
    {
        var dialog = await OpenAsync();

        Assert.Equal(2, dialog.FindAll(".gs-version").Count);

        var current = dialog.Find(".gs-version.is-current");

        Assert.Equal("Current", current.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// Who a version named and what they held, which is the whole of what the old panel left
    /// out -- and what it comes to, because 3 and 1 is not obviously 75/25.
    /// </summary>
    [Fact]
    public async Task A_version_names_the_people_and_what_each_of_them_held()
    {
        var dialog = await OpenAsync();

        var older = dialog.FindAll(".gs-version").Last();

        var weights = older.QuerySelectorAll(".gs-weight")
            .Select(row => (
                Who: row.QuerySelector(".gs-weight-who")!.TextContent.Trim(),
                Held: row.QuerySelector(".gs-weight-held")!.TextContent.Trim(),
                Share: row.QuerySelector(".gs-weight-share")!.TextContent.Trim()))
            .ToArray();

        Assert.Equal(("Ana Benitez", "3 shares", "75%"), weights[0]);
        Assert.Equal(("Lu Ferrer", "1 share", "25%"), weights[1]);
    }

    /// <summary>
    /// What one edit moved, per person, against the version it replaced.
    /// </summary>
    /// <remarks>
    /// Proportions rather than weights: 3 and 1 becoming an even split is a real change to
    /// what Lu owes, and comparing "3 shares" to "1 share" does not show it.
    /// </remarks>
    [Fact]
    public async Task The_current_version_says_what_it_moved()
    {
        var dialog = await OpenAsync();

        var deltas = dialog.Find(".gs-version.is-current")
            .QuerySelectorAll(".gs-delta")
            .Select(delta => delta.TextContent.Trim())
            .ToArray();

        Assert.Contains(deltas, delta => delta.Contains("Ana Benitez", StringComparison.Ordinal)
                                         && delta.Contains("75%", StringComparison.Ordinal)
                                         && delta.Contains("50%", StringComparison.Ordinal));

        // Lu went up, and the class is how that reads at a glance.
        Assert.Contains(
            dialog.Find(".gs-version.is-current").QuerySelectorAll(".gs-delta.is-up"),
            delta => delta.TextContent.Contains("Lu Ferrer", StringComparison.Ordinal));
    }

    /// <summary>
    /// The consequence somebody cannot see from a rule on its own: it may stand behind more
    /// than the category they arrived from.
    /// </summary>
    [Fact]
    public async Task A_rule_more_than_one_category_divides_by_says_so()
    {
        var dialog = await OpenAsync();

        var notice = dialog.Find(".gs-notice.is-warn");

        Assert.Equal("Groceries and Utilities all divide by this rule",
            notice.QuerySelector("strong")!.TextContent.Trim());
    }

    /// <summary>
    /// Going back to a division the group has used before is an ordinary edit, deliberately:
    /// it opens a new version rather than reopening the old one, so nothing already recorded
    /// moves and the history gains an entry saying the group went back -- which is what
    /// happened.
    /// </summary>
    [Fact]
    public async Task Dividing_an_old_way_again_saves_that_division_as_a_new_version()
    {
        var dialog = await OpenAsync();

        var restoring = dialog.FindAll("button")
            .First(button => button.TextContent.Trim() == "Divide this way again")
            .ClickAsync(new MouseEventArgs());

        // It asks first: this changes what two categories pre-fill from now on.
        await dialog.FindAll("button")
            .Last(button => button.TextContent.Trim() == "Use this division")
            .ClickAsync(new MouseEventArgs());

        await restoring;

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            Household,
            It.Is<UpdateSplitRuleRequest>(request =>
                request.Name == "Household 3-way" && request.Definition is SharesSplitRuleDto),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Somebody added to a rule went up, and is drawn as having gone up.
    /// </summary>
    /// <remarks>
    /// The older version does not name them, so their previous share is null, and `to > from`
    /// against a null is false whichever way it leans -- so a member being *added* was drawn
    /// in clay, the colour that means a share was cut. It is the commonest transition a rule
    /// has after a departure, and the colour is the whole point of the row.
    /// </remarks>
    [Fact]
    public async Task Somebody_the_older_version_did_not_name_reads_as_an_increase()
    {
        var marta = Guid.NewGuid();

        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, Flat, "Household 3-way",
            [
                new SplitRuleVersionResponse(Current, Closed, null, new EvenSplitRuleDto([Ana, Lu, marta])),
                new SplitRuleVersionResponse(Previous, Opened, Closed, new EvenSplitRuleDto([Ana, Lu]))
            ]));

        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new UserInfo(Ana, "Ana", "Benitez", null),
                new UserInfo(Lu, "Lu", "Ferrer", null),
                new UserInfo(marta, "Marta", "Ruiz", null)
            ]);

        var dialog = await OpenAsync();

        var added = dialog.Find(".gs-version.is-current")
            .QuerySelectorAll(".gs-delta")
            .Single(delta => delta.TextContent.Contains("Marta", StringComparison.Ordinal));

        Assert.Contains("none → 33.3%", added.TextContent, StringComparison.Ordinal);
        Assert.Contains("is-up", added.ClassName!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A division naming somebody who has gone cannot be put back, so it is not offered.
    /// </summary>
    /// <remarks>
    /// This is the commonest superseded version there is: the server closes the versions that
    /// name a departing member, so every one of them names somebody the API will now refuse
    /// in a rule. The button was offered on exactly those, over a confirmation promising the
    /// rule "will divide by this again from now on", and the only possible outcome was a
    /// refusal.
    /// </remarks>
    [Fact]
    public async Task A_division_naming_somebody_who_has_gone_is_not_offered_again()
    {
        var departed = Guid.NewGuid();

        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, Flat, "Household 3-way",
            [
                new SplitRuleVersionResponse(Current, Closed, null, new EvenSplitRuleDto([Ana, Lu])),

                new SplitRuleVersionResponse(Previous, Opened, Closed,
                    new SharesSplitRuleDto { Shares = { [Ana] = 1, [Lu] = 1, [departed] = 1 } })
            ]));

        var dialog = await OpenAsync();

        var button = dialog.FindAll("button")
            .Single(candidate => candidate.TextContent.Trim() == "Divide this way again");

        Assert.True(button.HasAttribute("disabled"));

        Assert.Contains("Names somebody no longer in the group", dialog.Markup, StringComparison.Ordinal);

        // And nothing is sent if it is reached anyway.
        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A membership that could not be read degrades to counts, and does not report everybody
    /// in the rule as a stranger to the group.
    /// </summary>
    /// <remarks>
    /// The failure is deliberately quiet -- a version with unnamed people in it is still worth
    /// reading, and a snackbar about a sentence would be noise. What made it a defect was the
    /// empty dictionary downstream: every lookup missed, and the timeline asserted that the
    /// whole membership of the rule was gone, on the screen whose purpose is saying who was on
    /// what.
    /// </remarks>
    [Fact]
    public async Task A_membership_that_could_not_be_read_names_nobody_rather_than_stranding_everybody()
    {
        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the network went away"));

        var dialog = await OpenAsync();

        Assert.DoesNotContain("not in the group", dialog.Markup, StringComparison.Ordinal);

        // The count is what not knowing looks like.
        Assert.Contains("By shares, between 2", dialog.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a failed count of the categories says so, because the absence of that warning is
    /// read as "there is nothing to warn about" immediately before an edit with fan-out.
    /// </summary>
    [Fact]
    public async Task A_failed_count_of_the_categories_is_said_rather_than_left_blank()
    {
        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the network went away"));

        var dialog = await OpenAsync();

        Assert.Contains("Could not check which categories divide by this rule", dialog.Markup,
            StringComparison.Ordinal);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<RuleHistoryDialog>
        {
            { dialog => dialog.SplitRuleId, Household }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<RuleHistoryDialog>("Split rule", parameters));

        return provider;
    }
}
