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
/// <param name="HasLink">
/// Whether a link to take this guest over is out and still works. False for one that has
/// expired, which <paramref name="LinkExpired"/> says.
/// </param>
/// <param name="Token">
/// The random part of that link, while it works. A bearer credential on a position in the
/// group's ledger, so it belongs to the people who can already see that ledger.
/// </param>
/// <param name="LinkIssuedAt">
/// When the latest link was issued -- the one out, or the one that expired.
/// </param>
/// <param name="LinkExpiresAt">
/// When that link stops working, or stopped: a guest's link lasts a fixed time from being
/// issued. Null when there is no link at all.
/// </param>
/// <param name="LinkExpired">
/// True when a link was issued and has run out, unclaimed. Issuing the link again
/// (<c>POST .../link</c>) makes a new one, with or without <c>replace</c>.
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
    DateTimeOffset? LinkIssuedAt,
    DateTimeOffset? LinkExpiresAt = null,
    bool LinkExpired = false);
