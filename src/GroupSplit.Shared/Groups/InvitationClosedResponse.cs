using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>How a guest's link stopped working, other than by being claimed.</summary>
public enum InvitationOutcome
{
    /// <summary>Whoever held it said it was not for them.</summary>
    Declined,

    /// <summary>A member stopped it.</summary>
    Withdrawn
}

/// <summary>
/// A guest's link that was turned down or stopped.
/// </summary>
/// <remarks>
/// Only the link ends: the guest stays, with everything recorded for them, and a member can
/// send a new link whenever they like. So nothing moves, and the counts below are always
/// zero -- they are kept, with <see cref="AbsorbedByUserId"/>, because clients built when
/// declining handed a guest's position to a member still read them.
/// </remarks>
/// <param name="InvitationId">
/// The id the guest had as an invitation, which the compatibility routes still take -- not
/// the user id every guest route is keyed by.
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
