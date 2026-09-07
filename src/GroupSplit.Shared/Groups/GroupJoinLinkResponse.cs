namespace GroupSplit.Shared;

/// <summary>
/// A group's standing join link, as its own members see it.
/// </summary>
/// <remarks>
/// Carries <see cref="Token"/>, because the whole point of the thing is to be copied. The
/// client builds the URL rather than the API: the address the app is published at is the
/// browser's own, and an API that guessed it would guess wrong for every deployment that is
/// not the one it was configured for.
/// </remarks>
public record GroupJoinLinkResponse(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Token,
    string? CreatedByUserName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// What a link says about itself to the person who followed it, before they join.
/// </summary>
/// <remarks>
/// Answering with the group's name is what makes the page worth showing: "Join Lisbon" is
/// something a person can agree to, and "join a group" is not. Holding the token is what
/// authorises knowing it, which is the same thing joining is authorised by.
/// </remarks>
public record JoinLinkResponse(
    Guid GroupId,
    string GroupName,
    int MemberCount,
    string? CreatedByUserName,
    DateTimeOffset ExpiresAt,
    bool AlreadyAMember);

/// <summary>
/// The group somebody has just joined by link.
/// </summary>
/// <remarks>
/// <see cref="AlreadyAMember"/> is the answer to following the same link twice, which is
/// the ordinary thing to do with a URL sitting in a chat thread. It is not a refusal: the
/// membership is the same one either way, and the caller shows "you are already in this
/// group" rather than an error.
/// </remarks>
public record JoinedGroupResponse(
    Guid GroupId,
    string GroupName,
    int MemberCount,
    bool AlreadyAMember);
