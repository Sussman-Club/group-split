using AngleSharp.Dom;
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
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<CancellationToken>()))
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

    private static IElement Rule(IRenderedComponent<GroupSplitsTab> tab, string name) =>
        tab.FindAll(".gs-rule").Single(rule => rule.QuerySelector(".title")!.TextContent.Trim() == name);

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
