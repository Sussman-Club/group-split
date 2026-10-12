using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
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
/// Creating a category, or renaming one and changing which rule it divides by.
/// </summary>
/// <remarks>
/// A category is a name and a pointer to one of the group's rules, and this dialog writes
/// both and nothing else -- the one it replaced wrote a fresh rule named after every category
/// it made. The settings tab test covers pointing at a rule and renaming from the tab; these
/// cover the dialog on its own: what it sends for each, what it checks before sending, and
/// that a name the group already uses leaves it open with the name still in the box.
/// </remarks>
public class CategoryDialogTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();

    private static readonly SplitRuleResponse Household = new(Guid.NewGuid(), Flat, "Household 3-way");
    private static readonly SplitRuleResponse WhoeverPaid = new(Guid.NewGuid(), Flat, "Whoever paid");

    private readonly List<(string Message, Severity Severity)> _said = [];

    public CategoryDialogTest()
    {
        Categories
            .Setup(client => client.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateCategoryRequest request, CancellationToken _) =>
                new CategoryResponse(Guid.NewGuid(), request.GroupId, request.Name, request.DefaultSplitRuleId, null));

        Categories
            .Setup(client => client.UpdateCategoryAsync(Groceries, It.IsAny<UpdateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, UpdateCategoryRequest request, CancellationToken _) =>
                new CategoryResponse(id, Flat, request.Name, request.DefaultSplitRuleId, null));

        Snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        // The real commands over the mocked client, so a refusal reaches the dialog the way it
        // does in the app: said by the presenter, and handed back as a null.
        Services.AddSingleton<ICategoryCommands, CategoryCommands>();
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync(
        CategoryResponse? editing = null, bool personal = false)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CategoryDialog>("Category", new DialogParameters<CategoryDialog>
                {
                    { d => d.GroupId, Flat },
                    { d => d.Category, editing },
                    { d => d.Rules, [Household, WhoeverPaid] },
                    { d => d.Personal, personal }
                }));

        return (provider, dialog);
    }

    private static Task NameAsync(IRenderedComponent<MudDialogProvider> provider, string name) =>
        provider.Find("input").InputAsync(new ChangeEventArgs { Value = name });

    private static Task DividesByAsync(IRenderedComponent<MudDialogProvider> provider, Guid? rule)
    {
        var select = provider.FindComponents<MudSelect<Guid?>>()
            .Single(candidate => candidate.Instance.Label == "Divides by");

        return provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(rule));
    }

    private static Task ClickAsync(IRenderedComponent<MudDialogProvider> provider, string label) =>
        provider.FindAll("button").Single(button => button.TextContent.Trim() == label)
            .ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task A_new_category_is_created_in_the_group_pointing_at_the_rule_picked()
    {
        var (provider, dialog) = await OpenAsync();

        Assert.Contains("New category", provider.Markup, StringComparison.Ordinal);

        await NameAsync(provider, "Groceries");
        await DividesByAsync(provider, Household.Id);
        await ClickAsync(provider, "Create");

        var created = await dialog.GetReturnValueAsync<CategoryResponse>();

        Assert.Equal("Groceries", created!.Name);

        Categories.Verify(client => client.CreateCategoryAsync(
            It.Is<CreateCategoryRequest>(request =>
                request.GroupId == Flat
                && request.Name == "Groceries"
                && request.DefaultSplitRuleId == Household.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The caption under the select says what each answer means, and no rule is an answer: an
    /// expense filed there divides evenly between the group.
    /// </summary>
    [Fact]
    public async Task With_no_rule_picked_it_says_the_expense_divides_evenly()
    {
        var (provider, _) = await OpenAsync();

        Assert.Contains("With no rule, an expense here is divided evenly", provider.Markup, StringComparison.Ordinal);

        await DividesByAsync(provider, Household.Id);

        Assert.Contains("Expenses filed here are pre-filled with this division", provider.Markup,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing opens on the category as it is, with its rule named rather than shown as the
    /// id the select holds, and saves through an update -- never a second category.
    /// </summary>
    [Fact]
    public async Task Editing_opens_on_the_category_as_it_is_and_saves_it_in_place()
    {
        var (provider, dialog) = await OpenAsync(
            new CategoryResponse(Groceries, Flat, "Groceries", Household.Id, Household.Name));

        Assert.Contains("Edit category", provider.Markup, StringComparison.Ordinal);

        var inputs = provider.FindAll("input");

        Assert.Equal("Groceries", inputs[0].GetAttribute("value"));
        Assert.Equal("Household 3-way", inputs[1].GetAttribute("value"));

        await NameAsync(provider, "Food");
        await DividesByAsync(provider, WhoeverPaid.Id);
        await ClickAsync(provider, "Save");

        Assert.Equal("Food", (await dialog.GetReturnValueAsync<CategoryResponse>())!.Name);

        Categories.Verify(client => client.UpdateCategoryAsync(
            Groceries,
            It.Is<UpdateCategoryRequest>(request =>
                request.Name == "Food" && request.DefaultSplitRuleId == WhoeverPaid.Id),
            It.IsAny<CancellationToken>()), Times.Once);

        Categories.Verify(client => client.CreateCategoryAsync(
            It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Taking the rule away is an edit in its own right, and goes over as a null rather than
    /// being dropped from the request.
    /// </summary>
    [Fact]
    public async Task Clearing_the_rule_sends_no_rule()
    {
        var (provider, dialog) = await OpenAsync(
            new CategoryResponse(Groceries, Flat, "Groceries", Household.Id, Household.Name));

        await DividesByAsync(provider, null);
        await ClickAsync(provider, "Save");

        await dialog.GetReturnValueAsync<CategoryResponse>();

        Categories.Verify(client => client.UpdateCategoryAsync(
            Groceries,
            It.Is<UpdateCategoryRequest>(request => request.Name == "Groceries" && request.DefaultSplitRuleId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A personal group has no rules and nobody to divide with, so the question is not asked
    /// -- and the API refuses a personal category that names one.
    /// </summary>
    [Fact]
    public async Task A_personal_category_is_not_asked_how_it_divides()
    {
        var (provider, dialog) = await OpenAsync(personal: true);

        Assert.Empty(provider.FindComponents<MudSelect<Guid?>>());

        await NameAsync(provider, "Groceries");
        await ClickAsync(provider, "Create");

        await dialog.GetReturnValueAsync<CategoryResponse>();

        Categories.Verify(client => client.CreateCategoryAsync(
            It.Is<CreateCategoryRequest>(request => request.Name == "Groceries" && request.DefaultSplitRuleId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The error path. The group's names are unique and only the server knows them all, so a
    /// duplicate comes back as a refusal: said once, with the dialog left open, the name
    /// still in the box and Create live again.
    /// </summary>
    [Fact]
    public async Task A_name_the_group_already_uses_is_refused_and_the_dialog_stays_open()
    {
        Categories
            .Setup(client => client.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.CategoryNameTaken));

        var (provider, dialog) = await OpenAsync();

        await NameAsync(provider, "Groceries");
        await ClickAsync(provider, "Create");

        Assert.False(dialog.Result.IsCompleted);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not create the category.", message, StringComparison.Ordinal);
        Assert.Contains(ErrorMessages.For(ErrorCodes.CategoryNameTaken), message, StringComparison.Ordinal);

        Assert.Equal("Groceries", provider.Find("input").GetAttribute("value"));

        var create = provider.FindAll("button").Single(button => button.TextContent.Trim() == "Create");
        Assert.False(create.HasAttribute("disabled"));
    }

    /// <summary>
    /// What the dialog checks itself before anything is sent: a name, and one that fits.
    /// </summary>
    [Theory]
    [InlineData("", "Name is required")]
    [InlineData("   ", "Name is required")]
    [InlineData("A name that goes on well past the sixty-four characters a category gets",
        "Name must be less than 64 characters.")]
    public async Task A_name_the_dialog_will_not_take_is_said_and_not_sent(string name, string said)
    {
        var (provider, dialog) = await OpenAsync();

        await NameAsync(provider, name);
        await ClickAsync(provider, "Create");

        Assert.Contains(said, provider.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);

        Categories.Verify(client => client.CreateCategoryAsync(
            It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancelling_writes_nothing()
    {
        var (provider, dialog) = await OpenAsync(
            new CategoryResponse(Groceries, Flat, "Groceries", Household.Id, Household.Name));

        await NameAsync(provider, "Food");
        await ClickAsync(provider, "Cancel");

        Assert.True((await dialog.Result)!.Canceled);

        Categories.Verify(client => client.UpdateCategoryAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateCategoryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
