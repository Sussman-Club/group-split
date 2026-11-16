namespace GroupSplit.Shared;

/// <summary>A group's expenses in one local calendar month, with personal amounts kept by currency.</summary>
public sealed record HouseholdMonthResponse(int Year, int Month, string TimeZone, DateTimeOffset From, DateTimeOffset Until,
    CategorySpendingSummaryResponse Spending, IReadOnlyList<HouseholdCurrencyExposure> Exposure);

public sealed record HouseholdCurrencyExposure(string Currency, decimal Paid, decimal Share);
