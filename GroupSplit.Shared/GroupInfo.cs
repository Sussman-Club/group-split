namespace GroupSplit.Shared;

/// <summary>
/// Information about a group including whether it's the user's personal group.
/// </summary>
public record GroupInfo(Guid Id, bool IsPersonal);