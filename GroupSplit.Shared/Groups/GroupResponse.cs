namespace GroupSplit.Shared;

/// <summary>
/// Information about a group including whether it's the user's personal group.
/// </summary>
public record GroupResponse(Guid Id, string Name, int MemberCount, bool IsArchive = false);