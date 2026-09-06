namespace GroupSplit.Shared;

/// <summary>
/// A standing invitation to join a group: somebody was asked by email, and has not yet
/// answered.
/// </summary>
/// <remarks>
/// Read from two directions, which is why it names both ends. The group's own page lists
/// the people it is waiting on -- <see cref="Email"/> is all it knows about them, since an
/// invited address need not belong to an account yet -- and the invitee's list needs
/// <see cref="GroupName"/> and <see cref="InvitedByUserName"/> to say what they are being
/// asked to join and by whom.
/// </remarks>
public record GroupInvitationResponse(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Email,
    string? InvitedByUserName,
    DateTimeOffset InvitedAt);
