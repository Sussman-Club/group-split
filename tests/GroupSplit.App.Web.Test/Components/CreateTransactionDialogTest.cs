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
/// Recording an expense, at the one place where the dialog says something the person could
/// not otherwise know: which rule the category they are about to pick divides by.
/// </summary>
/// <remarks>
/// Issue #245 asked for it in both transaction dialogs, and the two say it in their own
/// code -- this one off <see cref="CategoryResponse.DefaultSplitRuleName"/> directly, the
/// edit dialog off a row it builds -- so one test cannot stand for both.
/// </remarks>
public class CreateTransactionDialogTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IMerchantsClient> _merchants = new();
    private readonly Mock<ITransactionCommands> _commands = new();

    public CreateTransactionDialogTest()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(Guid.NewGuid(), "Ana", "Benitez", "ana@example.com")]);

        _merchants
            .Setup(m => m.GetMerchantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_merchants.Object);
        Services.AddSingleton<IMerchantCommands, MerchantCommands>();

        // Scoped, for the reason UpdateTransactionDialogTest gives: the real
        // TransactionCommands takes IDialogService, which MudBlazor registers scoped.
        Services.AddScoped(_ => _commands.Object);
    }

    /// <summary>
    /// Every category in the list names how it divides, and one that names no rule reads
    /// "even split" rather than nothing.
    /// </summary>
    /// <remarks>
    /// "Groceries" says nothing about how Groceries is divided, and somebody picking it is
    /// choosing both. The blank on a category with no rule was not a missing value either:
    /// no rule means an even split, which is the commonest arrangement in the app and was
    /// the one the dropdown said least about.
    /// </remarks>
    [Fact]
    public async Task The_category_select_says_how_each_category_divides()
    {
        var categories = new[]
        {
            new CategoryResponse(Guid.NewGuid(), GroupId, "Food", null, null),
            new CategoryResponse(Guid.NewGuid(), GroupId, "Rent", Guid.NewGuid(), "By room size")
        };

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(categories);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => categories.ToAsyncEnumerable());

        // The options live in a popover, which needs its host rendered: without one the
        // select shows only what is chosen, and the list under test is never built.
        var popovers = Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("New expense", parameters));

        // The category is a chip now, and its select opens underneath when the chip is
        // pressed -- so the list under test does not exist until somebody asks for it.
        await provider.FindAll("button.gs-chip")
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("category", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        var select = provider.FindComponents<MudSelect<Guid?>>()
            .Single(component => component.Instance.Label == "Category");

        await provider.InvokeAsync(() => select.Instance.OpenMenu());

        popovers.WaitForAssertion(() =>
        {
            Assert.Contains("By room size", popovers.Markup, StringComparison.Ordinal);
            Assert.Contains("even split", popovers.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Opened from inside a group, the group is a fact and not a question.
    /// </summary>
    /// <remarks>
    /// It used to be both: the heading said Home and a select underneath offered to change
    /// it. That is the same fact twice, with a decision attached that nobody standing in a
    /// group came to make.
    /// </remarks>
    [Fact]
    public async Task Opened_from_a_group_it_does_not_ask_which_group()
    {
        var provider = await OpenAsync(GroupId);

        Assert.DoesNotContain(Chips(provider),
            chip => chip.GetAttribute("aria-label")!.Contains("group", StringComparison.Ordinal));

        Assert.Contains("Trip", provider.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And opened from the expenses page, where there is no group yet, it is the one thing
    /// that has to be asked.
    /// </summary>
    [Fact]
    public async Task Opened_from_outside_a_group_the_group_is_the_first_chip()
    {
        var provider = await OpenAsync(null);

        Assert.Contains(Chips(provider),
            chip => chip.GetAttribute("aria-label")!.Contains("group", StringComparison.Ordinal));
    }

    /// <summary>
    /// There is no second step: the division is under the amount that decides it.
    /// </summary>
    /// <remarks>
    /// The split used to sit behind Next, which put the decision the app exists for one
    /// screen away from the figure it depends on -- and made the commonest expense in the
    /// app a five-gesture job.
    /// </remarks>
    [Fact]
    public async Task The_division_is_on_the_same_screen_as_the_amount()
    {
        var provider = await OpenAsync(GroupId);

        Assert.Single(provider.FindComponents<SplitEditor>());

        Assert.DoesNotContain(provider.FindAll("button"),
            button => button.TextContent.Trim() is "Next" or "Back");

        Assert.Contains(provider.FindAll("button"),
            button => button.TextContent.Trim() == "Add expense");
    }

    /// <summary>
    /// The note is a field, always on screen, and optional.
    /// </summary>
    /// <remarks>
    /// It is the one optional thing somebody types rather than picks, and it is what they
    /// reach for when an expense needs explaining. Behind a "more" disclosure it may as well
    /// not exist.
    /// </remarks>
    [Fact]
    public async Task A_note_can_be_written_without_going_looking_for_it()
    {
        var provider = await OpenAsync(GroupId);

        var note = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "Note");

        Assert.Equal("Optional", note.Instance.HelperText);
        Assert.False(note.Instance.Disabled);
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Chips(
        IRenderedComponent<MudDialogProvider> provider) =>
        [.. provider.FindAll("button.gs-chip")];

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(Guid? groupId)
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, groupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add expense", parameters));

        return provider;
    }
}
