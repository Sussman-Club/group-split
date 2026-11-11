using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Asks where some imported rows probably go. A page of the inbox asks for all its waiting
/// rows at once; the filing dialog asks for its one row, with the receipt once it is read.
/// </summary>
public sealed record SuggestFilingsRequest
{
    /// <summary>
    /// The most rows one request may ask about. Each is a call to the model, so a client
    /// with more asks in turns rather than holding one request open for all of them.
    /// </summary>
    public const int MaxRows = 50;

    /// <summary>The rows to suggest for.</summary>
    [Required]
    [Length(1, MaxRows, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.RowsLength))]
    public IReadOnlyList<SuggestFilingRow> Rows { get; init; } = [];
}

/// <summary>One row to suggest for, and what else is known about it.</summary>
public sealed record SuggestFilingRow
{
    public Guid Id { get; init; }

    /// <summary>
    /// What a receipt attached to the row says was bought, once somebody has read it.
    /// </summary>
    /// <remarks>
    /// Sent rather than looked up: reading a receipt is not stored until the row is filed,
    /// so the client holding the reading is the only one that has it.
    /// </remarks>
    public FilingReceipt? Receipt { get; init; }
}

/// <summary>
/// The parts of a read receipt that say what the money was for. Not the prices: the
/// row already has the amount, and the question is what was bought.
/// </summary>
public sealed record FilingReceipt
{
    [StringLength(128)]
    public string? MerchantName { get; init; }

    /// <summary>The line items' names, as the receipt printed them.</summary>
    [Length(0, MaxItems, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.ReceiptItemsLength))]
    public IReadOnlyList<string> Items { get; init; } = [];

    public const int MaxItems = 100;
}
