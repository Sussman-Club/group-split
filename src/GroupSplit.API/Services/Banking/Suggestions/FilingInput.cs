using GroupSplit.Shared;

namespace GroupSplit.API.Services.Banking.Suggestions;

/// <summary>The spending facts a classifier can use, independent of their source.</summary>
public sealed record FilingInput(
    Guid Id,
    DateOnly? Date,
    decimal Amount,
    string Currency,
    string Description,
    string? MerchantName = null,
    Guid? MerchantId = null,
    string? ProviderCategory = null,
    string? ProviderCategoryDetailed = null,
    string? AccountName = null,
    string? PaymentChannel = null,
    string? City = null);

public sealed record FilingSuggestionResult(Guid SourceId, FilingDestinationSuggestion Destination);
