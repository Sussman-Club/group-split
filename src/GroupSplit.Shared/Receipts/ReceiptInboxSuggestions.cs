using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public sealed record ReceiptInboxSuggestionsRequest
{
    public const int MaxRows = 50;
    [Required, Length(1, MaxRows)] public IReadOnlyList<Guid> ReceiptIds { get; init; } = [];
}

public sealed record ReceiptInboxSuggestion(Guid ReceiptId, FilingDestinationSuggestion Destination);
