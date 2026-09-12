using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the app says and does when a category or the split behind it is written.
/// </summary>
/// <remarks>
/// These are the commands issue #156 asked for, and they exist for a defect: the rules
/// dialog used to write through the generated clients and either announce itself or -- for
/// a split rule -- not announce at all. A category's name is printed against every expense
/// filed under it, so renaming Groceries to Food left every listing in the app saying
/// Groceries until it was reloaded.
/// <para>
/// So each of these asks two things of a write: that it announced, which is what makes the
/// pages catch up, and what it put in front of the person.
/// </para>
/// <para>
/// A rule used to stay quiet here, because it was only ever written as the division behind a
/// category and the category's own message covered both. The group's Splits tab edits them
/// apart -- one rule may stand behind several categories -- so a rule now speaks for itself,
/// and both of its messages say the same second thing: that nothing already recorded moved.
/// </para>
/// </remarks>
public class CategoryAndRuleCommandTest
{
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid FourWays = Guid.NewGuid();

    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<ISplitRulesClient> _rules = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();

    private readonly CategoryCommands _categoryCommands;
    private readonly SplitRuleCommands _ruleCommands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    private int _announced;

    public CategoryAndRuleCommandTest()
    {
        _snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        _changes.TransactionsChanged += () =>
        {
            _announced++;
            return Task.CompletedTask;
        };

        var errors = new ApiErrorPresenter(
            Mock.Of<IAuthService>(), new TestNavigationManager(), _snackbar.Object);

        _categoryCommands = new CategoryCommands(_categories.Object, errors, _snackbar.Object, _changes);
        _ruleCommands = new SplitRuleCommands(_rules.Object, errors, _snackbar.Object, _changes);
    }

    private static CategoryResponse ACategory(string name, Guid? ruleId = null, string? ruleName = null) =>
        new(Groceries, Trip, name, ruleId, ruleName);

    private static SplitRuleDetailsResponse ARule(string name) => new()
    {
        Id = FourWays,
        GroupId = Trip,
        Name = name,
        Definition = new EvenSplitRuleDto()
    };

    // ---- Categories -------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_category_says_which_one_and_tells_the_pages()
    {
        _categories
            .Setup(c => c.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateCategoryRequest request, CancellationToken _) => ACategory(request.Name));

        var created = await _categoryCommands.CreateAsync(new CreateCategoryRequest
        {
            GroupId = Trip,
            Name = "Groceries"
        });

        Assert.Equal("Groceries", created?.Name);
        Assert.Equal(1, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("Groceries created.", message);
    }

    /// <summary>
    /// The defect the command layer was asked for. A category's name is what an expense row
    /// shows, so a rename has to reach the pages holding those rows -- and this write used
    /// to reach nothing at all.
    /// </summary>
    [Fact]
    public async Task Renaming_a_category_announces_so_a_page_showing_it_catches_up()
    {
        _categories
            .Setup(c => c.UpdateCategoryAsync(Groceries, It.IsAny<UpdateCategoryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, UpdateCategoryRequest request, CancellationToken _) => ACategory(request.Name));

        var updated = await _categoryCommands.UpdateAsync(Groceries, new UpdateCategoryRequest
        {
            Name = "Food",
            DefaultSplitRuleId = FourWays
        });

        Assert.Equal("Food", updated?.Name);
        Assert.Equal(1, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Contains("Food", message, StringComparison.Ordinal);

        // The part somebody would otherwise have to test on their own history: changing how
        // a category divides does not reach backwards into what is already recorded.
        Assert.Contains("keep their split", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_category_says_which_one_and_tells_the_pages()
    {
        _categories
            .Setup(c => c.DeleteCategoryAsync(Groceries, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Assert.True(await _categoryCommands.DeleteAsync(Groceries, "Groceries"));
        Assert.Equal(1, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("Groceries deleted.", message);
    }

    /// <summary>
    /// A category with expenses under it cannot be deleted -- the API refuses rather than
    /// letting it take the group's history with it. That refusal is shown once, by the same
    /// presenter as everything else, and the caller is handed a false.
    /// </summary>
    [Fact]
    public async Task A_category_still_in_use_is_refused_once_and_nothing_is_announced()
    {
        _categories
            .Setup(c => c.DeleteCategoryAsync(Groceries, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.CategoryInUse));

        Assert.False(await _categoryCommands.DeleteAsync(Groceries, "Groceries"));
        Assert.Equal(0, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not delete the category.", message, StringComparison.Ordinal);
        Assert.Contains(ErrorMessages.For(ErrorCodes.CategoryInUse), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_group_already_uses_answers_null_rather_than_throwing()
    {
        _categories
            .Setup(c => c.CreateCategoryAsync(It.IsAny<CreateCategoryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.CategoryNameTaken));

        var created = await _categoryCommands.CreateAsync(new CreateCategoryRequest
        {
            GroupId = Trip,
            Name = "Groceries"
        });

        Assert.Null(created);
        Assert.Equal(0, _announced);
        Assert.Equal(Severity.Error, Assert.Single(_said).Severity);
    }

    [Fact]
    public async Task The_categories_of_a_group_are_read_through_the_command_too()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(Trip, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([ACategory("Groceries", FourWays, "Groceries")]);

        var loaded = await _categoryCommands.ForGroupAsync(Trip);

        Assert.Equal("Groceries", Assert.Single(loaded!).Name);
        Assert.Empty(_said);
    }

    /// <summary>
    /// Null is "do not open", and the dialog that asked closes itself. The reason is said
    /// on the way out, because a dialog that opens empty and silent is worse than one that
    /// says what it could not read.
    /// </summary>
    [Fact]
    public async Task A_failed_read_says_why_and_answers_null()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(Trip, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(404, ErrorCodes.GroupNotFound));

        Assert.Null(await _categoryCommands.ForGroupAsync(Trip));

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not load the categories.", message, StringComparison.Ordinal);
    }

    // ---- Split rules ------------------------------------------------------------------------

    /// <summary>
    /// A rule speaks for itself now.
    /// </summary>
    /// <remarks>
    /// It used to stay silent, because a rule was only ever written as the division behind a
    /// category and the category's own message said what had happened. They are edited apart
    /// on the group's Splits tab -- one rule may stand behind several categories -- so there
    /// is no category left to speak for it, and a save that said nothing read as one that
    /// did nothing.
    /// </remarks>
    [Fact]
    public async Task Saving_a_new_split_says_so_and_says_what_is_left_to_do()
    {
        _rules
            .Setup(c => c.CreateSplitRuleAsync(It.IsAny<CreateSplitRuleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateSplitRuleRequest request, CancellationToken _) => ARule(request.Name));

        var created = await _ruleCommands.CreateAsync(new CreateSplitRuleRequest
        {
            GroupId = Trip,
            Name = "Groceries",
            Definition = new EvenSplitRuleDto()
        });

        Assert.Equal(FourWays, created?.Id);
        Assert.Equal(1, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);

        // A rule nothing points at divides nothing. That is the next thing to do rather than
        // a fault, so the sentence carries it.
        Assert.Equal("Groceries created. Point a category at it to use it.", message);
    }

    /// <summary>
    /// And an edit says the part somebody would otherwise have to go and check.
    /// </summary>
    [Fact]
    public async Task Editing_a_split_says_that_what_is_recorded_is_untouched()
    {
        _rules
            .Setup(c => c.UpdateSplitRuleAsync(FourWays, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, UpdateSplitRuleRequest request, CancellationToken _) => ARule(request.Name));

        var updated = await _ruleCommands.UpdateAsync(FourWays, new UpdateSplitRuleRequest
        {
            Name = "Groceries",
            Definition = new EvenSplitRuleDto()
        });

        Assert.Equal(FourWays, updated?.Id);
        Assert.Equal(1, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);

        // The fear a person has when they change how something divides, answered in the
        // sentence rather than left to be tested by opening an expense from last March.
        Assert.Equal("Groceries updated. Expenses already recorded keep their split.", message);
    }

    /// <summary>
    /// Quiet on success, not on a refusal: a split the server will not take is the one thing
    /// about a rule the person has to be told, and it is told once.
    /// </summary>
    [Fact]
    public async Task A_split_the_server_refuses_is_shown_once_and_answers_null()
    {
        _rules
            .Setup(c => c.UpdateSplitRuleAsync(FourWays, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(400, ErrorCodes.SplitRuleInvalid));

        var updated = await _ruleCommands.UpdateAsync(FourWays, new UpdateSplitRuleRequest
        {
            Name = "Groceries",
            Definition = new EvenSplitRuleDto()
        });

        Assert.Null(updated);
        Assert.Equal(0, _announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not save the split.", message, StringComparison.Ordinal);
        Assert.Contains(ErrorMessages.For(ErrorCodes.SplitRuleInvalid), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_split_is_read_by_the_editor_through_the_command()
    {
        _rules
            .Setup(c => c.GetSplitRuleAsync(FourWays, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ARule("Groceries"));

        var rule = await _ruleCommands.GetAsync(FourWays);

        Assert.IsType<EvenSplitRuleDto>(rule?.Definition);
        Assert.Empty(_said);
    }

    [Fact]
    public async Task A_groups_splits_are_read_through_the_command_and_a_failure_answers_null()
    {
        _rules
            .Setup(c => c.GetSplitRulesAsync(Trip, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(FourWays, Trip, "Groceries")]);

        var loaded = await _ruleCommands.ForGroupAsync(Trip);

        Assert.Equal("Groceries", Assert.Single(loaded!).Name);

        _rules
            .Setup(c => c.GetSplitRulesAsync(Trip, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(404, ErrorCodes.GroupNotFound));

        Assert.Null(await _ruleCommands.ForGroupAsync(Trip));
        Assert.Equal(Severity.Error, Assert.Single(_said).Severity);
    }

    /// <summary>A navigation manager that goes nowhere, for a presenter that never navigates here.</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/groups");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
