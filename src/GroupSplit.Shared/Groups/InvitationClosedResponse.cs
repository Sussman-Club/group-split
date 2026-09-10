using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>How an invitation stopped being pending, other than by being accepted.</summary>
public enum InvitationOutcome
{
    /// <summary>The invitee turned it down.</summary>
    Declined,

    /// <summary>The group took it back.</summary>
    Withdrawn
}

/// <summary>
/// What became of an invitation that was declined or withdrawn, and of the money recorded
/// against it.
/// </summary>
/// <remarks>
/// A body rather than the 204 declining used to answer with, because there is now something
/// to say. Shares are recorded against an invited person from the moment the group names
/// them, so an invitation that goes away with money against it leaves that money belonging
/// to a stand-in nobody will ever sign in as -- and a group's balances have to keep adding
/// up. They are handed to <see cref="AbsorbedByUserName"/>: the member who sent the
/// invitation, or, if they are no longer here, the group's longest-standing member.
/// <para>
/// Nothing is re-divided and no amount changes. A share moves from one name to another, and
/// an expense the invitee had paid for becomes the absorber's; every transaction still
/// sums to its own amount, so the group's balances still sum to zero. The numbers here are
/// what moved, so whoever asked can say it out loud -- and the move itself is visible to
/// the whole group in the ledger, on the rows that changed hands.
/// </para>
/// </remarks>
/// <param name="SharesMoved">How many shares of an expense changed hands.</param>
/// <param name="AmountOwed">What those shares came to.</param>
/// <param name="PaymentsMoved">
/// How many transactions the invitee was down as having paid for, and now are not.
/// </param>
/// <param name="AmountPaid">What those came to.</param>
/// <param name="RulesAffected">
/// How many split rules named them, and no longer do. A rule is a template for the next
/// expense, so what was theirs is simply divided among the rest -- the same thing that
/// happens when a member leaves.
/// </param>
/// <param name="RulesEmptied">
/// How many of those rules now name nobody at all, which is the one number here that is a
/// warning rather than a receipt.
/// </param>
/// <remarks>
/// A rule with nobody left in it has changed what it means. A shares or percentage rule
/// stops dividing and refuses the next expense filed under it
/// (<c>SPLIT_RULE_INVALID</c>); an even one quietly becomes "between everybody", since
/// naming nobody is how an even split says that. Neither is a state anybody asked for, and
/// this is the moment it can still be said to the person who caused it.
/// </remarks>
/// <param name="AbsorbedByUserId">
/// Who took it all on, or null when there was nothing to take on.
/// </param>
public record InvitationClosedResponse(
    Guid InvitationId,
    Guid GroupId,
    string GroupName,
    string Name,
    InvitationOutcome Outcome,
    int SharesMoved,
    decimal AmountOwed,
    int PaymentsMoved,
    decimal AmountPaid,
    int RulesAffected,
    int RulesEmptied,
    Guid? AbsorbedByUserId,
    string? AbsorbedByUserName)
{
    /// <summary>Whether anything at all was recorded against the address.</summary>
    [JsonIgnore]
    public bool MovedAnything => SharesMoved > 0 || PaymentsMoved > 0 || RulesAffected > 0;
}
