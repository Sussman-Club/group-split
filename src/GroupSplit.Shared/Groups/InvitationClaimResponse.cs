using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// What a guest's link says about itself before anybody claims it.
/// </summary>
/// <remarks>
/// Deliberately silent about money. A link is a bearer credential on a position in a
/// group's ledger, and it can be forwarded, so what somebody holding one may learn is
/// exactly what they would learn by claiming it and looking: the group's name, its size, who
/// sent the link, and which guest it was made out for. What is <em>recorded</em> against
/// that person is the group's, and stays theirs until the claim makes it somebody's.
/// <para>
/// Naming the group is what makes the page worth showing: "You have been invited to Lisbon
/// as Carlos" is something a person can agree to, and "claim an invitation" is not.
/// </para>
/// </remarks>
/// <param name="InvitationId">
/// The id the guest had as an invitation, which the compatibility routes still take -- not
/// the user id every guest route is keyed by.
/// </param>
/// <param name="InvitedByUserName">Who sent the link: whoever issued it, or added the guest.</param>
/// <param name="InvitedAt">When the link was issued.</param>
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
/// personal link is not only joining: it moves a position -- shares, anything the person was
/// down as having paid for, and their places in the group's split rules -- from a stand-in
/// onto the claimer's own account. No amount changes, and every transaction still divides
/// into exactly its own amount, but somebody's balance has moved and the app should say so
/// rather than leaving it to be noticed.
/// </remarks>
/// <param name="RulesTaken">
/// How many split rules now name the claimer where they named the stand-in.
/// </param>
/// <param name="SettlementsCancelled">
/// Repayments recorded between the guest and the claimer, which would now read as the
/// claimer paying themselves, and so were deleted. They netted to nothing for the claimer,
/// so their balance is what it was.
/// </param>
public record InvitationClaimedResponse(
    Guid GroupId,
    string GroupName,
    int MemberCount,
    string Name,
    int SharesTaken,
    decimal AmountOwed,
    int PaymentsTaken,
    decimal AmountPaid,
    int RulesTaken,
    int SettlementsCancelled = 0,
    decimal AmountCancelled = 0m)
{
    /// <summary>Whether the group had recorded anything against them before they claimed it.</summary>
    [JsonIgnore]
    public bool TookAnything => SharesTaken > 0 || PaymentsTaken > 0 || RulesTaken > 0;

    /// <summary>
    /// Whether claiming took on something that will go on happening, rather than only a
    /// history.
    /// </summary>
    /// <remarks>
    /// The shares and the payments are the past: amounts already recorded, now theirs. A rule
    /// place is the future -- it gives them a share of every expense filed under that
    /// category from here on -- so it is the half of a claim somebody would most want
    /// pointed out, and the half that was silent until it was counted.
    /// </remarks>
    [JsonIgnore]
    public bool TookOnARule => RulesTaken > 0;
}
