namespace GroupSplit.Shared;

/// <summary>
/// What a listing adds up to, over the whole match rather than the page in hand. Read with
/// the same filter as the listing, so a client showing "38 expenses · $1,284.50" beside a
/// page of 25 is not quietly totalling the 25.
/// </summary>
public record TransactionSummaryResponse(int Count, decimal Total);
