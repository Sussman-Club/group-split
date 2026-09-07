using System.Text.Json;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the app says when somebody squares up with a whole group at once.
/// </summary>
/// <remarks>
/// The one-tap version writes several transfers behind a single press, so what it says
/// afterwards is the only account anybody gets of what it did: how many repayments and how
/// much in all. And being already square is not a fault -- somebody who presses this on a
/// settled group is told that, not shown a failure.
/// </remarks>
public class SettleUpCommandTest
{
    private static readonly Guid Trip = Guid.NewGuid();

    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly GroupCommands _commands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    public SettleUpCommandTest()
    {
        _snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        var errors = new ApiErrorPresenter(
            Mock.Of<IAuthService>(), new TestNavigationManager(), _snackbar.Object);

        _commands = new GroupCommands(_groups.Object, Mock.Of<IInvitationsClient>(), errors,
            _snackbar.Object, _changes);
    }

    /// <summary>A refusal shaped the way the generated client hands one over.</summary>
    private static ApiException<ProblemDetails> Refusal(int status, string code)
    {
        var members = new Dictionary<string, object?>
        {
            ["type"] = "https://groupsplit.app/errors/x",
            ["title"] = "A title for developers",
            ["status"] = status,
            ["detail"] = "A detail for developers",
            ["code"] = code,
            ["traceId"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            JsonSerializer.Serialize(members, Api), GroupSplitSerializer.Options)!;

        return new ApiException<ProblemDetails>("refused", status, JsonSerializer.Serialize(problem, Api),
            new Dictionary<string, IEnumerable<string>>(), problem, null!);
    }

    /// <summary>
    /// Both directions, because both are the caller's to state: what they paid and what they
    /// were paid. Neither line names two other members.
    /// </summary>
    private static SettleUpResponse Settled() => new()
    {
        Payments =
        [
            new SettlementPayment { ToUserName = "Daniel", Amount = 40.00m },
            new SettlementPayment { FromUserName = "Omar", Amount = 25.00m }
        ],
        Date = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
        Description = "End of September"
    };

    [Fact]
    public async Task Settling_up_says_how_many_repayments_and_how_much()
    {
        _groups
            .Setup(c => c.SettleUpAsync(Trip, It.IsAny<SettleUpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Settled());

        var settled = await _commands.SettleUpAsync(Trip, new SettleUpRequest());

        Assert.Equal(2, settled?.Payments.Count);
        Assert.Equal(65.00m, settled?.Total);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);

        // The count is the whole point of saying anything: one press wrote several
        // transfers, and this is the only place a person is told how many.
        Assert.Contains("2 repayments", message, StringComparison.Ordinal);
        Assert.Contains("65", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Transfers are transactions, so the group's figures have to be told -- the same
    /// announcement recording a single repayment makes.
    /// </summary>
    [Fact]
    public async Task Settling_up_tells_the_pages_the_transactions_moved()
    {
        _groups
            .Setup(c => c.SettleUpAsync(Trip, It.IsAny<SettleUpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Settled());

        var announced = false;
        _changes.TransactionsChanged += () => { announced = true; return Task.CompletedTask; };

        await _commands.SettleUpAsync(Trip, new SettleUpRequest());

        Assert.True(announced);
    }

    /// <summary>
    /// One repayment is not "1 repayments". A settling-up between two people is the ordinary
    /// case in a group of two, so the plural is not an edge.
    /// </summary>
    [Fact]
    public async Task One_repayment_is_said_in_the_singular()
    {
        _groups
            .Setup(c => c.SettleUpAsync(Trip, It.IsAny<SettleUpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettleUpResponse
            {
                Payments = [new SettlementPayment { ToUserName = "Daniel", Amount = 40.00m }],
                Date = DateTimeOffset.UtcNow
            });

        await _commands.SettleUpAsync(Trip, new SettleUpRequest());

        var (message, _) = Assert.Single(_said);

        Assert.Contains("1 repayment,", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_being_square_is_said_rather_than_shown_as_a_fault()
    {
        _groups
            .Setup(c => c.SettleUpAsync(Trip, It.IsAny<SettleUpRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusal(409, ErrorCodes.SettlementNothingToSettle));

        var announced = false;
        _changes.TransactionsChanged += () => { announced = true; return Task.CompletedTask; };

        // Null, so the dialog it came from can stay open rather than closing on a write that
        // did not happen.
        Assert.Null(await _commands.SettleUpAsync(Trip, new SettleUpRequest()));

        // Nothing moved, so nothing is asked to read itself again.
        Assert.False(announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("already square", message, StringComparison.Ordinal);
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
