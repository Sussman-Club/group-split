using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the expenses page shows when two spans are asked for and the answers come back in
/// the other order.
/// </summary>
/// <remarks>
/// The reported symptom was "I click This month, the right numbers show for a moment, and
/// then the old ones come back". Nothing in the page puts them back: what puts them back is
/// the answer to the previous question arriving second and being written down as though it
/// were the current one.
/// <para>
/// The group's Expenses tab already guards against this -- it keeps what it last asked
/// about and drops an answer that is no longer about the narrowing in force, and its remarks
/// describe this exact defect. The state service behind the personal expenses page had no
/// such guard, so a slow first answer overwrote a fast second one.
/// </para>
/// </remarks>
public class LateAnswerTest
{
    /// <summary>Held open so the first span's answer can be made to arrive last.</summary>
    private readonly TaskCompletionSource _firstAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Mock<ITransactionsClient> _client = new();
    private readonly TransactionsPageStateService _page;

    /// <summary>January, and February. Custom spans, so the bounds say which is which.</summary>
    private static readonly TransactionQuery January = TransactionQuery.Default with
    {
        Range = new DateFilter(DateFilterPreset.Custom, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31))
    };

    private static readonly TransactionQuery February = TransactionQuery.Default with
    {
        Range = new DateFilter(DateFilterPreset.Custom, new DateTime(2026, 2, 1), new DateTime(2026, 2, 28))
    };

    public LateAnswerTest()
    {
        _client
            .Setup(client => client.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset? from, DateTimeOffset? _, Guid? _, Guid? _, string? _, bool? _,
                string? _, string? _, bool? _, int? _, int? _, CancellationToken _) =>
            {
                await WaitIfJanuaryAsync(from);

                return new PagedResponseOfTransactionResponse([], 1, 25, Count(from));
            });

        _client
            .Setup(client => client.GetTransactionsSummaryAsync(It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset? from, DateTimeOffset? _, Guid? _, Guid? _, string? _, bool? _,
                string? _, CancellationToken _) =>
            {
                await WaitIfJanuaryAsync(from);

                return new TransactionSummaryResponse(Count(from), Total(from));
            });

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new TestNavigationManager(),
            Mock.Of<ISnackbar>());

        _page = new TransactionsPageStateService(_client.Object, new TransactionsTracker(),
            new LoadGuard(presenter), new DataChangeNotifier(), Mock.Of<ITransactionCommands>(),
            new LocalClock(Mock.Of<IJSRuntime>()));
    }

    /// <summary>January is the slow one, and stays slow until the test lets it answer.</summary>
    private Task WaitIfJanuaryAsync(DateTimeOffset? from) =>
        from?.Month == 1 ? _firstAnswer.Task : Task.CompletedTask;

    private static int Count(DateTimeOffset? from) => from?.Month switch
    {
        1 => 1,
        2 => 2,
        _ => 5
    };

    private static decimal Total(DateTimeOffset? from) => from?.Month switch
    {
        1 => 4.5m,
        2 => 8m,
        _ => 29m
    };

    [Fact]
    public async Task The_answer_to_the_span_that_was_left_behind_does_not_land_on_the_cards()
    {
        await _page.IsReadyTask;

        // January is asked for and does not answer yet -- the request is in flight.
        var january = _page.LoadAsync(January);

        // February is asked for and answers straight away: 2 expenses, 8.00. This is what
        // is on the cards, and what the list underneath is showing.
        await _page.LoadAsync(February);

        Assert.Equal(February, _page.Query);
        Assert.Equal(2, _page.MatchesSummary?.Count);

        // Now January answers. Nobody is looking at January.
        _firstAnswer.SetResult();
        await january;

        Assert.Equal(February, _page.Query);
        Assert.Equal(2, _page.MatchesSummary?.Count);
        Assert.Equal(8m, _page.MatchesSummary?.Total);
        Assert.Equal(2, _page.Page?.TotalCount);
    }

    /// <summary>A navigation manager that goes nowhere, for a presenter that never navigates here.</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/transactions");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
