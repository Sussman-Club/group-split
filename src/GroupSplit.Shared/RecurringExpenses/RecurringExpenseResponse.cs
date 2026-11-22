namespace GroupSplit.Shared;

/// <summary>
/// A repeating expense as its group sees it.
/// </summary>
/// <param name="Details">What each occurrence is, ready to send back changed.</param>
/// <param name="NextOn">The day of the next occurrence still to be created.</param>
/// <param name="EnteredByUserId">
/// Who the occurrences are entered by: whoever set it up, or last changed or resumed it.
/// </param>
/// <param name="Problem">
/// Why the occurrence on <paramref name="NextOn"/> could not be created, or null. It is tried
/// again every hour until it can be, so fixing the cause creates it on its own day.
/// </param>
public sealed record RecurringExpenseResponse(
    Guid Id,
    Guid GroupId,
    RecurringExpenseRequest Details,
    string PaidByUserName,
    Guid EnteredByUserId,
    DateOnly NextOn,
    bool Paused,
    string? Problem);
