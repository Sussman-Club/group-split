namespace GroupSplit.Shared;

/// <summary>
/// A group where the caller still owes money or is still owed it, and which therefore
/// stands in the way of deleting their account. Named rather than merely counted, so the
/// app can send someone to the groups they have to settle.
/// </summary>
/// <param name="Balance">
/// Negative when they owe the group, positive when the group owes them.
/// </param>
public record OutstandingBalance(Guid GroupId, string GroupName, decimal Balance);
