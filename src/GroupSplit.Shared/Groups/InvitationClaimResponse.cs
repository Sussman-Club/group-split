using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// What a personal invitation link says about itself before anybody claims it.
/// </summary>
/// <remarks>
/// Deliberately silent about money. A link is a bearer credential on a position in a
/// group's ledger, and it can be forwarded, so what somebody holding one may learn is
/// exactly what they would learn by claiming it and looking: the group's name, its size, who
/// asked, and which person the invitation was made out to. What is <em>recorded</em> against
/// that person is the group's, and stays theirs until the claim makes it somebody's.
/// <para>
/// Naming the group is what makes the page worth showing: "You have been invited to Lisbon
/// as Carlos" is something a person can agree to, and "claim an invitation" is not.
/// </para>
/// </remarks>
/// <param name="AlreadyAMember">
/// True when the claimer is already in the group. Claiming is still the right thing to do --
/// it is what makes the position theirs -- so this is a note for the page and not a refusal.
/// </param>
public record InvitationClaimResponse(
    Guid InvitationId,
    Guid GroupId,
    string GroupName,
    int MemberCount,
    string Name,
    string? InvitedByUserName,
    DateTimeOffset InvitedAt,
    bool AlreadyAMember);

/// <summary>
/// The group somebody has just joined by claiming a personal invitation, and what came with
/// it.
/// </summary>
/// <remarks>
/// The counts are the whole reason this is not <see cref="JoinedGroupResponse"/>. Claiming a
/// personal link is not only joining: it moves a position -- shares, and anything the person
/// was down as having paid for -- from a stand-in onto the claimer's own account. No amount
/// changes, and every transaction still divides into exactly its own amount, but somebody's
/// balance has moved and the app should say so rather than leaving it to be noticed.
/// </remarks>
public record InvitationClaimedResponse(
    Guid GroupId,
    string GroupName,
    int MemberCount,
    string Name,
    int SharesTaken,
    decimal AmountOwed,
    int PaymentsTaken,
    decimal AmountPaid)
{
    /// <summary>Whether the group had recorded anything against them before they claimed it.</summary>
    [JsonIgnore]
    public bool TookAnything => SharesTaken > 0 || PaymentsTaken > 0;
}
