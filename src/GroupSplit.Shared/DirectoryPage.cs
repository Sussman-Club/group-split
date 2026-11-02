namespace GroupSplit.Shared;

public sealed record DirectoryPage<T>(List<T> Items, bool HasMore);
