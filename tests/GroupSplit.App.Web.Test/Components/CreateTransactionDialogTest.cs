using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Users;
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
    private static readonly Guid MeId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IMerchantsClient> _merchants = new();
    private readonly Mock<ITransactionCommands> _commands = new();
    private readonly Mock<IUserLogin> _login = new();

    public CreateTransactionDialogTest()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(MeId, "Ana", "Benitez", "ana@example.com")]);

        // Whoever is recording the expense is who the dialog says paid for it, so it needs
        // to know who that is.
        _login.SetupGet(login => login.User)
            .Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        _merchants
            .Setup(m => m.GetMerchantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_merchants.Object);
        Services.AddSingleton(_login.Object);
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
        await provider.FindAll("button.gs-expense-chip")
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

    /// <summary>
    /// The commonest thing anybody does with this app: type an amount and a name, press the
    /// button, and get the expense the dialog was showing.
    /// </summary>
    /// <remarks>
    /// Nothing pinned it. Every other test here reads what the dialog renders, and the
    /// dialog was just rebuilt from two steps into one -- the amount moved to the top, the
    /// group became a field of its own rather than something inferred from the category, the
    /// validate-and-close that used to sit behind Next moved to the only button left. A
    /// rewrite of the path between typing and saving deserves one test that walks it, or the
    /// dialog can come apart in the way that costs the most and look perfectly correct in
    /// every assertion above.
    /// <para>
    /// The date especially. It is applied on submit and nowhere else, so a Save that skipped
    /// <c>ApplyDate</c> would file every expense at whatever instant the dialog happened to
    /// be constructed, which is a defect nothing on screen would show.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Typing_an_amount_and_a_name_records_that_expense_in_that_group()
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");

        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(42.50m));

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Mercadona"));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var request = await reference!.GetReturnValueAsync<CreateTransactionRequest>();

        Assert.NotNull(request);

        Assert.Equal(42.50m, request!.Amount);
        Assert.Equal("Mercadona", request.Name);

        // The group the dialog was opened in, which is the one thing on this screen nobody is
        // asked about and the one the expense is meaningless without.
        Assert.Equal(GroupId, request.GroupId);

        // Today, in the reader's zone -- read off the same clock the dialog picks its default
        // from, because that clock is not necessarily on the UTC day.
        var clock = Services.GetRequiredService<LocalClock>();

        Assert.Equal(clock.Today, clock.Local(request.DateTime).Date);
    }

    /// <summary>
    /// Opened from inside a group that named itself, the heading says which group without
    /// reading the list of every group the person belongs to.
    /// </summary>
    /// <remarks>
    /// The name used to be looked up in that list, so the one fact the dialog was certain
    /// of before it opened depended on a read about other groups entirely: slow left the
    /// heading blank, and failed left it blank for good, over a form whose group picker is
    /// not even shown.
    /// </remarks>
    [Fact]
    public async Task Opened_from_a_group_that_names_itself_it_asks_for_no_other_group()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId },
            { dialog => dialog.SelectedGroupName, "Trip" }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        Assert.Contains("Trip", provider.Markup, StringComparison.Ordinal);

        _groups.Verify(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The payer chip names whoever is recording the expense, not "somebody".
    /// </summary>
    /// <remarks>
    /// The API has always defaulted an absent payer to the caller, so this was never wrong
    /// in the ledger -- only on the screen, where the chip said "paid by somebody" and drew
    /// itself as a settled choice: no plus, no dashed border, nothing suggesting it wanted
    /// answering. The app was admitting to a gap it did not have, on the one field people
    /// are most likely to check before pressing Add.
    /// </remarks>
    [Fact]
    public async Task The_payer_is_whoever_is_recording_it()
    {
        var provider = await OpenAsync(GroupId);

        var payer = Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("payer", StringComparison.Ordinal));

        Assert.Contains("Ana", payer.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("somebody", payer.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picking a place puts its name on the chip.
    /// </summary>
    /// <remarks>
    /// The chip counts as filled the moment an id is set, and it draws its value rather
    /// than its label once it is -- so without the name resolved beside the id, choosing a
    /// shop replaced "+ where" with an empty pill and told a screen reader "where: .". The
    /// edit dialog looked the name up from the start; this one was given the chip without
    /// the lookup.
    /// </remarks>
    [Fact]
    public async Task Picking_where_it_was_spent_says_where_that_was()
    {
        var lidl = Guid.NewGuid();

        _merchants
            .Setup(m => m.GetMerchantAsync(lidl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MerchantResponse(lidl, "Lidl", null, DateTimeOffset.UtcNow, 0));

        var provider = await OpenAsync(GroupId);

        // The search lives in the chip's picker, which does not exist until it is opened.
        await Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("where", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());

        var picker = provider.FindComponent<MerchantPicker>();

        await provider.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(lidl));

        var where = Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("where", StringComparison.Ordinal));

        Assert.Contains("Lidl", where.TextContent, StringComparison.Ordinal);
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Chips(
        IRenderedComponent<MudDialogProvider> provider) =>
        [.. provider.FindAll("button.gs-expense-chip")];

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
