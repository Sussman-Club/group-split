namespace GroupSplit.Shared;

/// <summary>
/// What a listing adds up to, over the whole match rather than the page in hand. Read with
/// the same filter as the listing, so a client showing "38 expenses · $1,284.50" beside a
/// page of 25 is not quietly totalling the 25.
/// </summary>
/// <param name="Count">Every expense that matched, whatever currency it is in.</param>
/// <param name="Total">What the ones in <see cref="Currency"/> came to.</param>
/// <remarks>
/// One currency to a total, because there is no rate to add two with: a group keeps its
/// balances in one, but a personal expense may be in any, so a listing of what somebody
/// paid can hold dollars and euros side by side. Adding those up gives a number that is
/// not an amount of anything. <see cref="Total"/> is the headline currency's, and every
/// other currency in the match gets its own line in <see cref="OtherCurrencies"/> --
/// reported rather than dropped, so nothing that matched goes unaccounted for.
/// </remarks>
public record TransactionSummaryResponse(int Count, decimal Total)
{
    /// <summary>
    /// The currency <see cref="TransactionSummaryResponse.Total"/> is in: the default,
    /// unless nothing that matched is in it -- then whichever currency most of the matches
    /// are in, so a listing of one euro group reads in euros.
    /// </summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// The matches in any other currency, one entry per currency, never added into
    /// <see cref="TransactionSummaryResponse.Total"/>. Empty in the ordinary case.
    /// </summary>
    public IReadOnlyList<CurrencyTotalResponse> OtherCurrencies { get; init; } = [];
}

/// <summary>What the matches in one currency came to, kept apart from every other currency.</summary>
public record CurrencyTotalResponse(string Currency, int Count, decimal Total);
