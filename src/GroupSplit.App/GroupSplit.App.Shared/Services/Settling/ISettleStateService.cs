using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Settling;

/// <summary>
/// What the Settle page reads and writes: the cross-group plan, the history behind it, and
/// the one action that clears a person.
/// </summary>
/// <remarks>
/// A state service of its own rather than more surface on the groups one, because the axis
/// is different. The groups state holds one group at a time -- its balances, its expenses,
/// its members -- and everything here spans every group at once and is keyed by person.
/// </remarks>
public interface ISettleStateService
{
    /// <summary>Who to pay, who owes you, and the three figures over the top of both.</summary>
    SettlementPlanResponse? Plan { get; }

    /// <summary>Every repayment you were party to, newest first, one page at a time.</summary>
    PagedResponse<SettlementResponse>? History { get; }

    /// <summary>
    /// How many people are outstanding, in either direction. What the nav badge reads.
    /// </summary>
    /// <remarks>
    /// People rather than debts, because a person is what a payment has on the other end:
    /// four debts across two groups can be two payments, and a badge saying four would be
    /// counting the bookkeeping rather than the work.
    /// </remarks>
    int OutstandingCount { get; }

    /// <summary>Whether a read is in flight, for the page to dim itself with.</summary>
    bool IsLoading { get; }

    event Action? OnChanged;

    /// <summary>
    /// Reads the plan, once, and completes when that first read is done. Calling it again
    /// is free.
    /// </summary>
    /// <remarks>
    /// Explicit rather than something the constructor starts, for the same reason the inbox
    /// state makes it explicit: the nav renders for signed-out visitors too, and a read on
    /// their behalf answers 401 -- which the error presenter quite correctly turns into a
    /// trip to the sign-in page, from the nav, on every page.
    /// </remarks>
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the settlement history as well as the plan, from here on. The nav badge and
    /// the home page need only the plan, so the history waits until the Settle page opens.
    /// </summary>
    Task LoadHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads more of the history, keeping what is already shown.</summary>
    Task ShowMoreHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one payment between the caller and one person, across every group the debt
    /// between them spans.
    /// </summary>
    /// <returns>What was written, or null if it was refused.</returns>
    Task<SettleWithPersonResponse?> SettleAsync(SettleWithPersonRequest request, string personName,
        CancellationToken cancellationToken = default);

    /// <summary>Re-reads the plan, and the history if anything has asked for it.</summary>
    Task RefreshAsync();
}
