using GroupSplit.App.Shared.Localization;
namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// The words for the period a settlement was made through: "through 31 Aug".
/// </summary>
/// <remarks>
/// One place, like <see cref="ParticipantCopy"/>, because every list that shows a settlement
/// -- the ledger, the home page's activity, the settle page's history -- says it after the
/// same note, and they should read the same.
/// </remarks>
public static class SettlementPeriodCopy
{
    extension(DateOnly? coversThrough)
    {
        /// <summary>
        /// <c>" · through 31 Aug"</c> after a settlement's note, or nothing for one that
        /// claims no period.
        /// </summary>
        public string ThroughSuffix() =>
            coversThrough is { } day ? UiText.Format($" · through {day:d MMM}") : string.Empty;
    }
}
