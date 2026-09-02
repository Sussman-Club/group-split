namespace GroupSplit.Shared;

public record GroupResponse(Guid Id, string Name, int MemberCount, bool IsArchive = false);