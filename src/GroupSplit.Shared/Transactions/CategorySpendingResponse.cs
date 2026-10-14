namespace GroupSplit.Shared;

/// <summary>What a group spent in one category over a filtered period.</summary>
public record CategorySpendingResponse(Guid? CategoryId, string Name, int Count, decimal Total, decimal Share);

/// <summary>
/// Spending by category, kept one currency at a time so amounts in different currencies
/// are never added together.
/// </summary>
public record CategorySpendingSummaryResponse(IReadOnlyList<CategorySpendingResponse> Categories)
{
    /// <summary>The currency of <see cref="Categories"/>.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Matches in currencies other than <see cref="Currency"/>.</summary>
    public IReadOnlyList<CategoryCurrencySpendingResponse> OtherCurrencies { get; init; } = [];
}

/// <summary>The category rows for one non-headline currency.</summary>
public record CategoryCurrencySpendingResponse(string Currency, IReadOnlyList<CategorySpendingResponse> Categories);
