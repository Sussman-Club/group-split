namespace GroupSplit.Shared;

/// <summary>
/// One of a group's guests: somebody it shares costs with who has no account in it.
/// </summary>
/// <remarks>
/// Read by the group's members only, because of <see cref="Token"/>.
/// </remarks>
/// <param name="UserId">
/// The guest's id everywhere else: the one their shares are recorded against, the one to name
/// as a payer, in a split or in a repayment, and the one to remove them by. The same id the
/// group's members listing shows them under, marked as a guest.
/// </param>
/// <param name="Name">What the group calls them.</param>
/// <param name="HasLink">Whether a link to take this guest over is out.</param>
/// <param name="Token">
/// The random part of that link, when there is one. A bearer credential on a position in the
/// group's ledger, so it belongs to the people who can already see that ledger.
/// </param>
public record GroupGuestResponse(
    Guid UserId,
    Guid GroupId,
    string GroupName,
    string Name,
    string? AddedByUserName,
    DateTimeOffset AddedAt,
    bool HasLink,
    string? Token,
    string? LinkIssuedByUserName,
    DateTimeOffset? LinkIssuedAt);
