using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Splits tab: its categories, the divisions they point at, and the writes
/// somebody can make from it.
/// </summary>
/// <remarks>
/// It replaces a dialog called "Manage Rules" that managed categories. One row per category,
/// with the division behind it edited in the same form and saved under the category's own
/// name -- so a rule never appeared on screen, and the shape the model was rebuilt for could
/// not be asked for: several categories dividing by one rule, changed in one place. The
/// rename was worse than a gap. It wrote the category's new name into the rule, which in a
/// group where two categories shared one would have renamed the division under both.
/// </remarks>
public class GroupSplitsTabTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();

    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Utilities = Guid.NewGuid();
    private static readonly Guid DiningOut = Guid.NewGuid();

    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid WhoeverPaid = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();

    public GroupSplitsTabTest()
    {
        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            [
                new CategoryResponse(Groceries, Flat, "Groceries", Household, "Household 3-way"),
                new CategoryResponse(Utilities, Flat, "Utilities", Household, "Household 3-way"),
                new CategoryResponse(DiningOut, Flat, "Dining out", null, null)
            ]);

        Categories
            .Setup(client => client.UpdateCategoryAsync(It.IsAny<Guid>(), It.IsAny<UpdateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateCategoryRequest request, CancellationToken _) =>
                new CategoryResponse(id, Flat, request.Name, request.DefaultSplitRuleId, null));

        SplitRules
            .Setup(client => client.GetSplitRulesAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SplitRuleResponse(Household, Flat, "Household 3-way"),
                new SplitRuleResponse(WhoeverPaid, Flat, "Whoever paid")
            ]);

        SplitRules
            .Setup(client => client.GetSplitRuleAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(Household, "Household 3-way", new EvenSplitRuleDto()));

        SplitRules
            .Setup(client => client.GetSplitRuleAsync(WhoeverPaid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(WhoeverPaid, "Whoever paid", new PayerSplitRuleDto()));

        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(Guid.NewGuid(), "Ana", "Benitez", null)]);

        Services.AddSingleton(_groups.Object);

        // The real commands over the mocked clients: what reaches the API, and what is said
        // about it, is the thing under test.
        Services.AddSingleton<ICategoryCommands, CategoryCommands>();
        Services.AddSingleton<ISplitRuleCommands, SplitRuleCommands>();
    }

    private static SplitRuleDetailsResponse ARule(Guid id, string name, SplitRuleDto definition) => new()
    {
        Id = id,
        GroupId = Flat,
        Name = name,
        VersionId = Guid.NewGuid(),
        ChangedAt = DateTimeOffset.UtcNow,
        Definition = definition
    };

    /// <summary>
    /// A category says which division it points at, and one that points at none says what
    /// that means rather than leaving the column blank.
    /// </summary>
    [Fact]
    public async Task Each_category_says_what_it_divides_by()
    {
        var tab = await RenderTabAsync();

        var rows = tab.FindAll(".gs-row .meta").Select(row => row.TextContent.Trim()).ToArray();

        Assert.Contains("divides by Household 3-way", rows);

        // Not a gap: a category with no rule is ordinary, and it divides evenly.
        Assert.Contains(rows, row => row.Contains("no rule", StringComparison.Ordinal)
                                     && row.Contains("evenly", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rule names the categories that divide by it, which is the fact that makes editing
    /// one something to think about -- and the one the old dialog could not show at all.
    /// </summary>
    [Fact]
    public async Task A_rule_names_every_category_that_divides_by_it()
    {
        var tab = await RenderTabAsync();

        var household = Rule(tab, "Household 3-way");

        var used = household.QuerySelectorAll(".gs-tag").Select(tag => tag.TextContent.Trim()).ToArray();

        Assert.Equal(["Groceries", "Utilities"], used);
    }

    /// <summary>
    /// And a rule nothing points at says so, rather than showing an empty row of tags that
    /// reads as a rule in use.
    /// </summary>
    [Fact]
    public async Task A_rule_no_category_points_at_says_so()
    {
        var tab = await RenderTabAsync();

        Assert.Equal("No category uses it", Rule(tab, "Whoever paid").QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// The write the old dialog could not make: pointing a category at a rule that already
    /// exists, rather than minting a twin of it under the category's own name.
    /// </summary>
    [Fact]
    public async Task A_category_can_be_pointed_at_a_rule_that_already_exists()
    {
        var (tab, provider) = await RenderWithDialogsAsync();

        // Not awaited yet: the click runs until the dialog it opens closes, so awaiting it
        // here would wait for a dialog nothing has answered.
        var editing = Edit(tab, "Dining out").ClickAsync(new MouseEventArgs());

        var picker = provider.FindComponent<MudSelect<Guid?>>();

        await provider.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(WhoeverPaid));
        await Button(provider, "Save").ClickAsync(new MouseEventArgs());
        await editing;

        Categories.Verify(client => client.UpdateCategoryAsync(
            DiningOut,
            It.Is<UpdateCategoryRequest>(request =>
                request.Name == "Dining out" && request.DefaultSplitRuleId == WhoeverPaid),
            It.IsAny<CancellationToken>()), Times.Once);

        // The point of the whole shape: pointing at a division writes nothing to it.
        SplitRules.Verify(client => client.CreateSplitRuleAsync(
            It.IsAny<CreateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Renaming a category leaves the rule behind it alone.
    /// </summary>
    /// <remarks>
    /// The dialog this replaces sent the category's new name to the rule as well. Groceries
    /// and Utilities divide by one rule here, so under the old behaviour renaming either of
    /// them would have renamed the division under both -- and the category left holding it
    /// would have shown a name nobody had typed against it.
    /// </remarks>
    [Fact]
    public async Task Renaming_a_category_does_not_rename_the_rule_behind_it()
    {
        var (tab, provider) = await RenderWithDialogsAsync();

        var editing = Edit(tab, "Groceries").ClickAsync(new MouseEventArgs());

        await provider.FindAll("input").First().InputAsync(new ChangeEventArgs { Value = "Food" });
        await Button(provider, "Save").ClickAsync(new MouseEventArgs());
        await editing;

        Categories.Verify(client => client.UpdateCategoryAsync(
            Groceries,
            It.Is<UpdateCategoryRequest>(request =>
                request.Name == "Food" && request.DefaultSplitRuleId == Household),
            It.IsAny<CancellationToken>()), Times.Once);

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A read that failed is not allowed to assert that there is nothing there.
    /// </summary>
    /// <remarks>
    /// On a first load the list it keeps is the empty one it started with, and every claim
    /// downstream of it then reads as a fact about the group: "No categories yet", "No
    /// category uses it", and -- worst -- Delete enabled on rules a category still points
    /// at, which is the action the tab's own tooltip logic exists to withhold. The snackbar
    /// that named the failure has usually scrolled away by the time somebody reaches the
    /// second column, so the state has to be on the screen.
    /// </remarks>
    [Fact]
    public async Task A_failed_read_says_so_and_stops_the_screen_asserting_anything()
    {
        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the network went away"));

        var tab = await RenderTabAsync();

        Assert.Contains("This did not all load", tab.Markup, StringComparison.Ordinal);

        // None of the three claims an empty list would otherwise have made.
        Assert.DoesNotContain("No categories yet", tab.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No category uses it", tab.Markup, StringComparison.Ordinal);

        Assert.All(
            tab.FindAll("button").Where(button =>
                button.GetAttribute("aria-label")?.StartsWith("Delete ", StringComparison.Ordinal) == true),
            button => Assert.True(button.HasAttribute("disabled")));
    }

    /// <summary>
    /// A category's caption comes from the rule it points at, so the two columns cannot
    /// disagree about the same fact.
    /// </summary>
    /// <remarks>
    /// The caption used to read the name the categories listing carried alongside the id,
    /// while the used-by tags and the delete gate read the id. The two lists are separate
    /// reads, so they can disagree: a rule renamed between them leaves the category carrying
    /// the old name, and the same screen then captions a category with a rule name that
    /// appears nowhere in the column beside it.
    /// </remarks>
    [Fact]
    public async Task A_category_is_captioned_from_the_rule_it_points_at()
    {
        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(DiningOut, Flat, "Dining out", WhoeverPaid, "What it used to be called")]);

        var tab = await RenderTabAsync();

        Assert.Equal("divides by Whoever paid", tab.Find(".gs-row .meta").TextContent.Trim());

        Assert.Equal(["Dining out"],
            Rule(tab, "Whoever paid").QuerySelectorAll(".gs-tag").Select(tag => tag.TextContent.Trim()));
    }

    /// <summary>
    /// A membership that could not be read leaves rules described by count, not by naming
    /// everybody in them as a stranger to the group.
    /// </summary>
    /// <remarks>
    /// The mirror of the same defect in the rule history dialog, and it outlived the fix
    /// there: this screen kept an empty dictionary where that one moved to null. Every
    /// lookup then misses, and a weighted rule reads "By shares — someone not in the group
    /// 3, someone not in the group 1" on the tab whose job is saying how the group divides.
    /// </remarks>
    [Fact]
    public async Task A_membership_that_could_not_be_read_describes_rules_by_count()
    {
        SplitRules
            .Setup(client => client.GetSplitRuleAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(Household, "Household 3-way",
                new SharesSplitRuleDto { Shares = { [Guid.NewGuid()] = 3, [Guid.NewGuid()] = 1 } }));

        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the network went away"));

        var tab = await RenderTabAsync();

        var described = Rule(tab, "Household 3-way").QuerySelector(".meta")!.TextContent;

        Assert.Contains("By shares, between 2", described, StringComparison.Ordinal);
        Assert.DoesNotContain("not in the group", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// And with the membership in hand, a rule's line names the people in it.
    /// </summary>
    [Fact]
    public async Task A_rule_is_described_with_the_people_it_names()
    {
        var ana = Guid.NewGuid();
        var lu = Guid.NewGuid();

        SplitRules
            .Setup(client => client.GetSplitRuleAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(Household, "Household 3-way",
                new SharesSplitRuleDto { Shares = { [ana] = 3, [lu] = 1 } }));

        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new UserInfo(ana, "Ana", "Benitez", null),
                new UserInfo(lu, "Lu", "Ferrer", null)
            ]);

        var described = Rule(await RenderTabAsync(), "Household 3-way").QuerySelector(".meta")!.TextContent;

        Assert.Contains("By shares — Ana Benitez 3, Lu Ferrer 1", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Delete is withheld on a rule a category still points at: the API refuses one in use,
    /// and an action that can only fail is worse than none.
    /// </summary>
    [Fact]
    public async Task Delete_is_withheld_on_a_rule_a_category_points_at()
    {
        var tab = await RenderTabAsync();

        Assert.True(Delete(tab, "Household 3-way").HasAttribute("disabled"));

        // And offered on one nothing points at, or the gate would just be "never".
        Assert.False(Delete(tab, "Whoever paid").HasAttribute("disabled"));
    }

    /// <summary>
    /// Opening a rule two categories divide by warns that it stands behind both -- before
    /// anything is saved, and while Cancel is still the way out.
    /// </summary>
    /// <remarks>
    /// The fan-out is the whole reason a rule is a thing of its own rather than a field on a
    /// category, and it is the one consequence somebody cannot see from inside the editor:
    /// they arrived from Groceries and the form in front of them says nothing about
    /// Utilities. The tab holds the join already, so the warning costs a parameter -- and the
    /// absence of one is read as "there is nothing to warn about" at exactly the moment that
    /// matters.
    /// <para>
    /// Driven from the tab rather than by rendering the dialog directly, because the
    /// parameter is the thing that can break: the tab passes null while its categories are
    /// unread, and a wiring that passed the wrong list, or none, would leave the dialog
    /// correct and the screen silent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Editing_a_rule_two_categories_divide_by_names_both_before_the_save()
    {
        var (tab, provider) = await RenderWithDialogsAsync();

        // Not awaited yet: the click runs until the dialog closes, so awaiting here would
        // wait on a dialog nothing has answered.
        var editing = tab.FindAll("button")
            .First(button => button.GetAttribute("aria-label") == "Edit Household 3-way")
            .ClickAsync(new MouseEventArgs());

        var warning = provider.FindAll(".gs-notice.is-warn")
            .Single(notice => notice.QuerySelector("strong") is not null);

        Assert.Equal("Groceries and Utilities all divide by this rule",
            warning.QuerySelector("strong")!.TextContent.Trim());

        // It is said while there is still a way out, which is the only time saying it helps.
        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        await Button(provider, "Cancel").ClickAsync(new MouseEventArgs());
        await editing;
    }

    private static IElement Rule(IRenderedComponent<GroupSplitsTab> tab, string name) =>
        tab.FindAll(".gs-rule").Single(rule => rule.QuerySelector(".title")!.TextContent.Trim() == name);

    /// <summary>
    /// A rule that divides evenly can be opened, read and saved.
    /// </summary>
    /// <remarks>
    /// The editor offered three kinds -- personal, percent, shares -- because until this tab
    /// existed the only way to reach a rule was through the category that owned it, and every
    /// category the app created made one of those three. The API has always had an even rule
    /// and the CLI has always written one, so a group can hold one; opening it gave a form
    /// with no kind selected, no fields, and a Save that validated to false and returned
    /// without a word. An Edit button that opens a form nobody can save is the defect the
    /// Delete button beside it is withheld to avoid.
    /// </remarks>
    [Fact]
    public async Task An_even_rule_opens_with_its_kind_selected_and_saves()
    {
        SplitRules
            .Setup(client => client.GetSplitRuleAsync(WhoeverPaid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(WhoeverPaid, "Whoever paid", new EvenSplitRuleDto()));

        SplitRules
            .Setup(client => client.UpdateSplitRuleAsync(WhoeverPaid, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateSplitRuleRequest request, CancellationToken _) =>
                ARule(id, request.Name, request.Definition));

        var (tab, provider) = await RenderWithDialogsAsync();

        var editing = tab.FindAll("button")
            .First(button => button.GetAttribute("aria-label") == "Edit Whoever paid")
            .ClickAsync(new MouseEventArgs());

        // The kind it already is, selected -- not an empty box the person has to guess at.
        Assert.Equal(RuleType.Even, provider.FindComponent<MudSelect<RuleType?>>().Instance.Value);

        await Button(provider, "Save").ClickAsync(new MouseEventArgs());
        await editing;

        // And Save saved, rather than validating to false and returning in silence.
        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            WhoeverPaid,
            It.Is<UpdateSplitRuleRequest>(request => request.Definition is EvenSplitRuleDto),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Retiring a category, which is what people mean by deleting one they have been using.
    /// </summary>
    /// <remarks>
    /// The API refuses to delete a category with anything filed under it, so for any
    /// category older than a week this is the only door that opens -- and the screen has to
    /// show the retired ones or there would be no way back through it.
    /// </remarks>
    [Fact]
    public async Task A_category_can_be_retired_without_touching_what_is_filed_under_it()
    {
        Categories
            .Setup(client => client.ArchiveCategoryAsync(DiningOut, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryResponse(DiningOut, Flat, "Dining out", null, null, IsArchive: true));

        var tab = await RenderTabAsync();

        await tab.FindAll("button")
            .First(button => button.GetAttribute("aria-label") == "Archive Dining out")
            .ClickAsync(new MouseEventArgs());

        Categories.Verify(client => client.ArchiveCategoryAsync(DiningOut, It.IsAny<CancellationToken>()),
            Times.Once);

        // Nothing is deleted on the way past: the expenses filed under it are the reason
        // this exists at all.
        Categories.Verify(client => client.DeleteCategoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// This screen asks for the retired ones; every picker asks for what the group uses.
    /// </summary>
    [Fact]
    public async Task The_tab_asks_for_archived_categories_and_marks_them()
    {
        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new CategoryResponse(Groceries, Flat, "Groceries", Household, "Household 3-way"),
                new CategoryResponse(DiningOut, Flat, "Dining out", null, null, IsArchive: true)
            ]);

        var tab = await RenderTabAsync();

        Categories.Verify(client => client.GetCategoriesAsync(Flat, true, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        var retired = tab.FindAll(".gs-row .title")
            .Single(row => row.TextContent.Contains("Dining out", StringComparison.Ordinal));

        Assert.Equal("Archived", retired.QuerySelector(".gs-tag")!.TextContent.Trim());

        // And it offers the way back rather than the way in.
        Assert.Contains(tab.FindAll("button"),
            button => button.GetAttribute("aria-label") == "Restore Dining out");
    }

    private static IElement Delete(IRenderedComponent<GroupSplitsTab> tab, string what) =>
        tab.FindAll("button").First(button => button.GetAttribute("aria-label") == $"Delete {what}");

    private static IElement Edit(IRenderedComponent<GroupSplitsTab> tab, string category) =>
        tab.FindAll("button").First(button =>
            button.GetAttribute("aria-label") == $"Edit {category}");

    private static IElement Button(IRenderedComponent<MudDialogProvider> provider, string text) =>
        provider.FindAll("button").Last(button => button.TextContent.Trim() == text);

    private async Task<IRenderedComponent<GroupSplitsTab>> RenderTabAsync()
    {
        var (tab, _) = await RenderWithDialogsAsync();
        return tab;
    }

    /// <summary>
    /// The tab, and the provider the dialogs it opens render into. Two roots in one context,
    /// which is how a component that opens dialogs is driven: they share the dialog service.
    /// </summary>
    private async Task<(IRenderedComponent<GroupSplitsTab> Tab, IRenderedComponent<MudDialogProvider> Dialogs)>
        RenderWithDialogsAsync()
    {
        var provider = Render<MudDialogProvider>();

        var tab = Render<GroupSplitsTab>(parameters => parameters
            .Add(component => component.GroupId, Flat)
            .Add(component => component.GroupName, "The flat"));

        // The rules' definitions are read one call per rule, after the lists are on screen.
        await tab.InvokeAsync(() => Task.CompletedTask);

        return (tab, provider);
    }
}
