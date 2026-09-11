using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The two writes that say where an expense's shares came from, and where a group's expenses
/// should point.
/// </summary>
/// <remarks>
/// Neither moves a penny, and that is exactly why what they say matters: the figures on
/// screen are identical afterwards, so a bare "updated" would read as a button that did
/// nothing. Both sentences carry it.
/// <para>
/// They are also the two endpoints the CLI has driven since rules were versioned and no
/// client could reach -- so the app could record the wrong provenance and never correct it.
/// See <c>docs/cli.md</c> under "Saying what divided it" and "Re-pointing a back catalogue".
/// </para>
/// </remarks>
public class DivisionSourceCommandTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Expense = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly DataChangeNotifier _changes = new();

    private readonly TransactionCommands _commands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    private int _announced;

    public DivisionSourceCommandTest()
    {
        _snackbar
            .Setup(bar => bar.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
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

        _commands = new TransactionCommands(
            _transactions.Object, errors, _snackbar.Object, _dialogs.Object, _changes);
    }

    // ---- What divided one expense ------------------------------------------------------------

    [Fact]
    public async Task Recording_that_a_version_divided_it_sends_the_version_and_says_nothing_moved()
    {
        _transactions
            .Setup(client => client.SetTransactionDivisionSourceAsync(Expense,
                It.IsAny<SetDivisionSourceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse { Id = Expense, Name = "Mercadona" });

        var done = await _commands.DivisionSourceAsync(Expense, Version, "Mercadona");

        Assert.True(done);

        _transactions.Verify(client => client.SetTransactionDivisionSourceAsync(
            Expense,
            It.Is<SetDivisionSourceRequest>(request => request.SplitRuleVersionId == Version),
            It.IsAny<CancellationToken>()), Times.Once);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("Recorded what divided Mercadona. No amount moved.", message);

        // Nothing a listing prints has changed, and the pages are told anyway: this decides
        // what the next edit of that expense does, and the dialogs that make it read the
        // expense from the listings.
        Assert.Equal(1, _announced);
    }

    /// <summary>
    /// The other answer: nobody's rule produced these amounts.
    /// </summary>
    /// <remarks>
    /// Null on the wire, and it has to be sent rather than left out -- it is the whole
    /// content of "--hand-split". The sentence says what it costs, because an expense whose
    /// shares are its own has no rule to be re-billed under and a later amount edit is
    /// refused rather than restating them.
    /// </remarks>
    [Fact]
    public async Task Recording_that_the_amounts_are_the_expenses_own_sends_null()
    {
        _transactions
            .Setup(client => client.SetTransactionDivisionSourceAsync(Expense,
                It.IsAny<SetDivisionSourceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse { Id = Expense, Name = "Mercadona" });

        await _commands.DivisionSourceAsync(Expense, null, "Mercadona");

        _transactions.Verify(client => client.SetTransactionDivisionSourceAsync(
            Expense,
            It.Is<SetDivisionSourceRequest>(request => request.SplitRuleVersionId == null),
            It.IsAny<CancellationToken>()), Times.Once);

        var (message, _) = Assert.Single(_said);

        Assert.Equal("Mercadona's shares are recorded as its own. No amount moved.", message);
    }

    // ---- Re-pointing a back catalogue --------------------------------------------------------

    /// <summary>
    /// A dry run saves nothing, so it says nothing and tells nobody.
    /// </summary>
    /// <remarks>
    /// It is the whole safety of the operation: somebody reads what would move before any of
    /// it does. A success message over a run that wrote nothing would be a lie about the
    /// most consequential button in the group.
    /// </remarks>
    [Fact]
    public async Task A_dry_run_is_silent_and_announces_nothing()
    {
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ASummary(dryRun: true, changed: 812));

        var summary = await _commands.ReattachAsync(Flat, dryRun: true);

        Assert.Equal(812, summary?.Changed);

        _transactions.Verify(client => client.ReattachTransactionsAsync(
            It.Is<ReattachTransactionsRequest>(request => request.GroupId == Flat && request.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Empty(_said);
        Assert.Equal(0, _announced);
    }

    [Fact]
    public async Task Applying_it_counts_what_moved_and_says_no_amount_did()
    {
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ASummary(dryRun: false, changed: 812));

        await _commands.ReattachAsync(Flat, dryRun: false);

        _transactions.Verify(client => client.ReattachTransactionsAsync(
            It.Is<ReattachTransactionsRequest>(request => request.GroupId == Flat && !request.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("812 expenses re-pointed. No amount moved.", message);
        Assert.Equal(1, _announced);
    }

    /// <summary>One is an expense, not "1 expenses".</summary>
    [Fact]
    public async Task One_expense_is_counted_in_the_singular()
    {
        _transactions
            .Setup(client => client.ReattachTransactionsAsync(It.IsAny<ReattachTransactionsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ASummary(dryRun: false, changed: 1));

        await _commands.ReattachAsync(Flat, dryRun: false);

        var (message, _) = Assert.Single(_said);

        Assert.Equal("1 expense re-pointed. No amount moved.", message);
    }

    private static ReattachSummaryResponse ASummary(bool dryRun, int changed) =>
        new(Flat, dryRun, Examined: 1411, Changed: changed, LeftWithoutAVersion: 63, ByRule: []);

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
