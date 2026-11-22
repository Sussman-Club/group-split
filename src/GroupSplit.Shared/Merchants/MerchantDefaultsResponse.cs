namespace GroupSplit.Shared;

/// <summary>
/// Where an expense at one merchant usually goes, for an expense typed in by hand.
/// </summary>
/// <param name="Destination">
/// The group and the category, or the rule the expense chose for itself where it had no
/// category; null when there is nothing to go on.
/// </param>
/// <param name="FromExpenseId">
/// The expense it was read from: the caller's most recent one at this merchant in a group
/// they are still in. Null when it is the classifier's guess instead, which is offered and
/// never filled in by itself.
/// </param>
/// <param name="FromExpenseDate">When that expense was, so the app can say "as on 3 Sep".</param>
public sealed record MerchantDefaultsResponse(
    FilingDestinationSuggestion? Destination,
    Guid? FromExpenseId = null,
    DateTimeOffset? FromExpenseDate = null);
