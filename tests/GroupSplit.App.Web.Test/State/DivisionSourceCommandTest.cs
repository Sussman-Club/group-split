using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The write that says where an expense's shares came from.
/// </summary>
/// <remarks>
/// It moves no penny, and that is exactly why what it says matters: the figures on screen
/// are identical afterwards, so a bare "updated" would read as a button that did nothing.
/// Both sentences carry it.
/// <para>
/// It is the endpoint the CLI has driven since rules were versioned and no client could
/// reach -- so the app could record the wrong provenance and never correct it. See
/// <c>docs/cli.md</c> under "Saying what divided it".
/// </para>
/// </remarks>
public class DivisionSourceCommandTest
{
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

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
