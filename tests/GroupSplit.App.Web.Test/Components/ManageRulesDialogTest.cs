using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The categories dialog, driven through the dialogs it opens: the group's labels, the
/// split behind each one, and the three writes somebody can make from it.
/// </summary>
/// <remarks>
/// It used to call the generated clients itself, keep its own error alert and announce a
/// category change while saying nothing at all about a rule -- so a rename left every
/// listing in the app showing the old label. These drive the real dialog over mocked
/// clients and ask what reached the API, what was said, and whether the pages were told.
/// See issue #156, and <see cref="State.CategoryAndRuleCommandTest"/> for the commands
/// underneath on their own.
/// </remarks>
public class ManageRulesDialogTest : ComponentTest
{
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid GroceriesRule = Guid.NewGuid();

    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<ISplitRulesClient> _rules = new();
    private readonly Mock<IGroupsClient> _groups = new();

    private readonly List<(string Message, Severity Severity)> _said = [];

    private int _announced;

    public ManageRulesDialogTest()
    {
        Snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        _categories
            .Setup(client => client.GetCategoriesAsync(Trip, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [new CategoryResponse(Groceries, Trip, "Groceries", GroceriesRule, "Groceries")]);

        _categories
            .Setup(client => client.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateCategoryRequest request, CancellationToken _) =>
                new CategoryResponse(Guid.NewGuid(), Trip, request.Name, request.DefaultSplitRuleId, request.Name));

        _categories
            .Setup(client => client.UpdateCategoryAsync(It.IsAny<Guid>(), It.IsAny<UpdateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateCategoryRequest request, CancellationToken _) =>
                new CategoryResponse(id, Trip, request.Name, request.DefaultSplitRuleId, request.Name));

        _categories
            .Setup(client => client.DeleteCategoryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _rules
            .Setup(client => client.GetSplitRulesAsync(Trip, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(GroceriesRule, Trip, "Groceries")]);

        _rules
            .Setup(client => client.GetSplitRuleAsync(GroceriesRule, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule(GroceriesRule, "Groceries"));

        _rules
            .Setup(client => client.CreateSplitRuleAsync(It.IsAny<CreateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateSplitRuleRequest request, CancellationToken _) =>
                ARule(Guid.NewGuid(), request.Name));

        _rules
            .Setup(client => client.UpdateSplitRuleAsync(It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateSplitRuleRequest request, CancellationToken _) =>
                ARule(id, request.Name));

        _groups
            .Setup(client => client.GetGroupMembersAsync(Trip, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(Guid.NewGuid(), "Ana", "Benitez", null)]);

        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_rules.Object);
        Services.AddSingleton(_groups.Object);

        // The real commands over the mocked clients: the announcement and the message are
        // the thing under test, and they live in the commands.
        Services.AddSingleton<ICategoryCommands, CategoryCommands>();
        Services.AddSingleton<ISplitRuleCommands, SplitRuleCommands>();

        Changes.TransactionsChanged += () =>
        {
            _announced++;
            return Task.CompletedTask;
        };
    }

    private static SplitRuleDetailsResponse ARule(Guid id, string name) => new()
    {
        Id = id,
        GroupId = Trip,
        Name = name,
        Definition = new PayerSplitRuleDto()
    };

    /// <summary>
    /// A dialog renders nothing on its own -- the provider is what puts one on screen -- so
    /// these open it the way the group page does, and read the whole provider afterwards.
    /// </summary>
    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<ManageRulesDialog> { { dialog => dialog.GroupId, Trip } };

        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>()
            .ShowAsync<ManageRulesDialog>("Manage Rules", parameters));

        return provider;
    }

    private static IElement Button(IRenderedComponent<MudDialogProvider> provider, string text) =>
        provider.FindAll("button").Last(button => button.TextContent.Trim() == text);

    /// <summary>The two icon buttons on the row: edit, then delete.</summary>
    private static IElement RowButton(IRenderedComponent<MudDialogProvider> provider, int index) =>
        provider.FindAll("tbody button")[index];

    private static IReadOnlyList<string> Rows(IRenderedComponent<MudDialogProvider> provider) =>
        [.. provider.FindAll("tbody td:first-child").Select(cell => cell.TextContent.Trim())];

    /// <summary>
    /// Fills in the editor the create and edit dialogs share: a name, and a division simple
    /// enough to need no numbers typed into it.
    /// </summary>
    private static async Task DescribeAsync(IRenderedComponent<MudDialogProvider> provider, string category)
    {
        await provider.FindAll("input").First().InputAsync(new ChangeEventArgs { Value = category });

        var type = provider.FindComponent<MudSelect<RuleType?>>();

        await provider.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(RuleType.Personal));
    }

    [Fact]
    public async Task The_list_shows_what_the_group_files_its_spending_under()
    {
        var provider = await OpenAsync();

        Assert.Equal(["Groceries"], Rows(provider));
        Assert.Contains("Groceries", provider.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A category and its split are two writes, and the person made one change: both reach
    /// the API, one sentence comes back, and the pages are told.
    /// </summary>
    [Fact]
    public async Task Creating_a_category_writes_the_split_and_the_label_and_says_so_once()
    {
        var provider = await OpenAsync();

        var creating = Button(provider, "Create category").ClickAsync(new MouseEventArgs());

        await DescribeAsync(provider, "Utilities");
        await Button(provider, "Create").ClickAsync(new MouseEventArgs());
        await creating;

        _rules.Verify(client => client.CreateSplitRuleAsync(
            It.Is<CreateSplitRuleRequest>(request => request.Name == "Utilities" && request.GroupId == Trip),
            It.IsAny<CancellationToken>()), Times.Once);

        _categories.Verify(client => client.CreateCategoryAsync(
            It.Is<CreateCategoryRequest>(request => request.Name == "Utilities" && request.DefaultSplitRuleId != null),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Contains("Utilities", Rows(provider));

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("Utilities created.", message);

        // Twice, because two things were written; what matters is that it is not zero, which
        // is what a rule write used to announce.
        Assert.Equal(2, _announced);
    }

    /// <summary>
    /// The refusal somebody actually meets here: a name the group already uses. It is shown
    /// once, by the same presenter as everything else, and the dialog stays open on the list
    /// it had -- there is nothing to close over, since the name is still wrong.
    /// </summary>
    [Fact]
    public async Task A_name_the_group_already_uses_is_shown_once_and_the_list_is_left_alone()
    {
        _categories
            .Setup(client => client.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.CategoryNameTaken));

        var provider = await OpenAsync();

        var creating = Button(provider, "Create category").ClickAsync(new MouseEventArgs());

        await DescribeAsync(provider, "Groceries");
        await Button(provider, "Create").ClickAsync(new MouseEventArgs());
        await creating;

        Assert.Equal(["Groceries"], Rows(provider));

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not create the category.", message, StringComparison.Ordinal);

        // Still there, with its table, rather than closed over an error.
        Assert.Contains("Manage categories", provider.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect the issue was filed for. Renaming a category renames it on every expense
    /// filed under it, and this write reached nothing: the group page kept the old label
    /// until it was reloaded.
    /// </summary>
    [Fact]
    public async Task Renaming_a_category_saves_both_halves_and_tells_the_pages()
    {
        var provider = await OpenAsync();

        var editing = RowButton(provider, 0).ClickAsync(new MouseEventArgs());

        await DescribeAsync(provider, "Food");
        await Button(provider, "Edit").ClickAsync(new MouseEventArgs());
        await editing;

        _rules.Verify(client => client.UpdateSplitRuleAsync(GroceriesRule,
            It.Is<UpdateSplitRuleRequest>(request => request.Name == "Food"),
            It.IsAny<CancellationToken>()), Times.Once);

        _categories.Verify(client => client.UpdateCategoryAsync(Groceries,
            It.Is<UpdateCategoryRequest>(request => request.Name == "Food"),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(["Food"], Rows(provider));
        Assert.Equal(2, _announced);
        Assert.Contains("Food", Assert.Single(_said).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_category_takes_the_row_out_and_tells_the_pages()
    {
        var provider = await OpenAsync();

        var deleting = RowButton(provider, 1).ClickAsync(new MouseEventArgs());

        await Button(provider, "Delete").ClickAsync(new MouseEventArgs());
        await deleting;

        _categories.Verify(client => client.DeleteCategoryAsync(Groceries, It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Empty(Rows(provider));
        Assert.Equal(1, _announced);
        Assert.Equal("Groceries deleted.", Assert.Single(_said).Message);
    }

    /// <summary>
    /// Nothing to manage and no reason to sit there: the presenter has said why, and the
    /// dialog closes rather than showing an empty table.
    /// </summary>
    [Fact]
    public async Task A_group_whose_categories_cannot_be_read_does_not_open()
    {
        _categories
            .Setup(client => client.GetCategoriesAsync(Trip, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(404, ErrorCodes.GroupNotFound));

        var provider = await OpenAsync();

        Assert.DoesNotContain("Manage categories", provider.Markup, StringComparison.Ordinal);
        Assert.Contains("Could not load the categories.", Assert.Single(_said).Message, StringComparison.Ordinal);
    }
}
