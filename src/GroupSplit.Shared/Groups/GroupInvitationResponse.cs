namespace GroupSplit.Shared;

/// <summary>
/// A standing invitation: somebody the group has named, given a link, and not heard back
/// from.
/// </summary>
/// <remarks>
/// Read from the group's side only, which is the difference from the version this replaces.
/// An invitation used to be an address, so the invitee could be shown a list of the groups
/// waiting on them; a named person with a link has no account to attach such a list to, and
/// the link is how they find out.
/// </remarks>
/// <param name="Name">
/// What the group calls them. The only thing it knows: nobody has signed in as this person,
/// so there is no profile to read a name out of.
/// </param>
/// <param name="Token">
/// The random part of the URL to send them. A bearer credential on a position in the
/// group's ledger, so it belongs to the people who can already see that ledger and nowhere
/// else -- and it stops working the moment somebody claims it.
/// </param>
/// <param name="ParticipantUserId">
/// Who to name to give this invitee a share: the id their shares are recorded against until
/// somebody claims the link. The same id the group's members listing shows them under,
/// marked as not having joined.
/// </param>
public record GroupInvitationResponse(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Name,
    string Token,
    string? InvitedByUserName,
    DateTimeOffset InvitedAt,
    Guid ParticipantUserId);
