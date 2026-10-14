using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's own settings, on the Settings page rather than in a dialog: its name, the
/// split an uncategorised expense falls back to, and archiving.
/// </summary>
/// <remarks>
/// It was a dialog behind an Edit button on a card that only described it. An inline form
/// has a question a dialog does not: when is there anything to save? So these are mostly
/// about Save -- disabled until something differs from the group, and sending only what
/// differs when pressed.
/// </remarks>
public class GroupDetailsCardTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Even = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private readonly Mock<IGroupsPageStateService> _state = new();

    private JsonPatchDocument<CreateGroupRequest>? _written;

    public GroupDetailsCardTest()
    {
        _state
            .Setup(state => state.UpdateGroupAsync(It.IsAny<JsonPatchDocument<CreateGroupRequest>>(),
                It.IsAny<CancellationToken>()))
            .Callback((JsonPatchDocument<CreateGroupRequest> patch, CancellationToken _) => _written = patch)
            .ReturnsAsync(true);

        _state.Setup(state => state.ArchiveGroupAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _state.Setup(state => state.UnarchiveGroupAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Services.AddSingleton(_state.Object);
    }

    /// <summary>
    /// The settings carry different weight, and each says what it is for before
    /// offering a field. The by-the-bill rule is not offered as a default: it defers to a
    /// receipt, and a default has to divide an expense that was typed in with nothing
    /// attached.
    /// </summary>
    [Fact]
    public void It_explains_both_settings_and_offers_no_bill_rule_as_default()
    {
        var card = Render(new GroupResponse(Flat, "The flat", 3) { DefaultSplitRuleId = Even });

        Assert.Equal(4, card.FindAll(".gs-group-setting-section").Count);
        Assert.Contains("The name your members see throughout the app.", card.Markup, StringComparison.Ordinal);
        Assert.Contains("The span every member's Activity tab starts on.", card.Markup, StringComparison.Ordinal);
        Assert.Contains("Optional context used when suggesting where a bank row belongs.", card.Markup, StringComparison.Ordinal);
        Assert.Contains("Default split", card.Markup, StringComparison.Ordinal);
        Assert.Contains("Used when an expense has no category.", card.Markup, StringComparison.Ordinal);
        Assert.Contains("Categories with their own split keep using it.", card.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Receipt items", card.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to save until something differs from the group, and no Discard to offer
    /// either. A Save button that is always live invites pressing it to find out.
    /// </summary>
    [Fact]
    public void Save_is_disabled_until_something_changes()
    {
        var card = Render(new GroupResponse(Flat, "The flat", 3) { DefaultSplitRuleId = Even });

        Assert.True(Save(card).HasAttribute("disabled"));
        Assert.DoesNotContain(card.FindAll("button"), button => button.TextContent.Contains("Discard"));
    }

    /// <summary>
    /// A changed name goes to the server as a patch of the name alone -- the default split
    /// is not restated -- and the form settles on what it saved, so Save goes quiet again.
    /// </summary>
    [Fact]
    public async Task Saving_a_new_name_patches_only_the_name()
    {
        var card = Render(new GroupResponse(Flat, "The flat", 3) { DefaultSplitRuleId = Even });

        await card.Find("input").InputAsync(new ChangeEventArgs { Value = "The bigger flat" });

        Assert.False(Save(card).HasAttribute("disabled"));
        Assert.Contains(card.FindAll("button"), button => button.TextContent.Contains("Discard"));

        await Save(card).ClickAsync(new());

        Assert.NotNull(_written);
        var operation = Assert.Single(_written.Operations);
        Assert.EndsWith("Name", operation.path, StringComparison.Ordinal);
        Assert.Equal("The bigger flat", operation.value);

        Assert.True(Save(card).HasAttribute("disabled"));
    }

    /// <summary>
    /// A blank name is refused on the form rather than sent to be refused by the API.
    /// </summary>
    [Fact]
    public async Task A_blank_name_is_not_sent()
    {
        var card = Render(new GroupResponse(Flat, "The flat", 3));

        await card.Find("input").InputAsync(new ChangeEventArgs { Value = "" });
        await Save(card).ClickAsync(new());

        Assert.Null(_written);
        _state.Verify(state => state.UpdateGroupAsync(It.IsAny<JsonPatchDocument<CreateGroupRequest>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Discard puts the group's own values back without a round trip.
    /// </summary>
    [Fact]
    public async Task Discard_returns_to_what_the_group_says()
    {
        var card = Render(new GroupResponse(Flat, "The flat", 3));

        await card.Find("input").InputAsync(new ChangeEventArgs { Value = "Something else" });
        await card.FindAll("button").First(button => button.TextContent.Contains("Discard")).ClickAsync(new());

        Assert.Equal("The flat", card.Find("input").GetAttribute("value"));
        Assert.True(Save(card).HasAttribute("disabled"));
        Assert.Null(_written);
    }

    /// <summary>
    /// Archiving is one button that reads for the state the group is in, and pressing it
    /// goes straight to the write: nothing to confirm, because the same button undoes it.
    /// </summary>
    [Fact]
    public async Task The_archive_button_follows_the_group_and_writes_at_once()
    {
        var live = Render(new GroupResponse(Flat, "The flat", 3));
        await live.FindAll("button").First(button => button.TextContent.Contains("Archive group")).ClickAsync(new());
        _state.Verify(state => state.ArchiveGroupAsync(It.IsAny<CancellationToken>()), Times.Once);

        var archived = Render(new GroupResponse(Flat, "The flat", 3, IsArchive: true));
        Assert.Contains("Archived", archived.Find(".gs-card-title").TextContent, StringComparison.Ordinal);
        await archived.FindAll("button").First(button => button.TextContent.Contains("Restore group")).ClickAsync(new());
        _state.Verify(state => state.UnarchiveGroupAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The default split reads as a name from the first frame, never as the raw id.
    /// </summary>
    /// <remarks>
    /// The select's options are the rules, and the settings page reads those after it
    /// renders. With no option carrying the bound id yet, the control fell back to
    /// printing the value it was given -- so opening Settings flashed a GUID where the
    /// rule's name belongs. The group's response already names its default, so there is
    /// something to say in that first frame.
    /// </remarks>
    [Fact]
    public void The_default_split_never_shows_its_raw_id()
    {
        var card = Render<GroupDetailsCard>(parameters => parameters
            .Add(component => component.Group,
                new GroupResponse(Flat, "The flat", 3)
                {
                    DefaultSplitRuleId = Even,
                    DefaultSplitRuleName = "Even"
                })
            .Add(component => component.Loaded, false)
            .Add(component => component.Rules, []));

        Assert.DoesNotContain(Even.ToString(), card.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Even", card.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And nothing at all where the group names no default, rather than the empty Guid.
    /// </summary>
    [Fact]
    public void An_unset_default_shows_nothing()
    {
        var card = Render<GroupDetailsCard>(parameters => parameters
            .Add(component => component.Group, new GroupResponse(Flat, "The flat", 3))
            .Add(component => component.Loaded, false)
            .Add(component => component.Rules, []));

        Assert.DoesNotContain(Guid.Empty.ToString(), card.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private IRenderedComponent<GroupDetailsCard> Render(GroupResponse group) =>
        Render<GroupDetailsCard>(parameters => parameters
            .Add(card => card.Group, group)
            .Add(card => card.Loaded, true)
            .Add(card => card.Rules, new[]
            {
                new SplitRuleResponse(Even, Flat, "Even", BuiltIn: true, DividesEvenly: true),
                new SplitRuleResponse(Household, Flat, "Household 3-way"),
                new SplitRuleResponse(Guid.NewGuid(), Flat, "Receipt items", DividesByBill: true)
            }));

    private static AngleSharp.Dom.IElement Save(IRenderedComponent<GroupDetailsCard> card) =>
        card.FindAll("button").First(button => button.TextContent.Contains("Save changes"));
}
