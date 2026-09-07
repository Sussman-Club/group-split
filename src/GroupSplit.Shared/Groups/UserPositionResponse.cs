namespace GroupSplit.Shared;

/// <summary>
/// Where one person stands across every group they are in -- the tracker half of the
/// question the group pages answer one group at a time.
/// </summary>
/// <remarks>
/// <see cref="Net"/> is the figure the home page leads with, and it is not
/// <see cref="OwedToYou"/> minus <see cref="YouOwe"/> by coincidence: the two are the
/// positive and negative group balances added up separately, so a person who is owed 40 in
/// one group and owes 25 in another sees both facts rather than only the 15 that survives
/// them. Netting the two into one number is what a bank statement does; this is a position.
/// </remarks>
public record UserPositionResponse(
    decimal Net,
    decimal OwedToYou,
    decimal YouOwe,
    IReadOnlyList<GroupPosition> Groups);

/// <summary>
/// What one group comes to for the person asking.
/// </summary>
/// <param name="Balance">Positive when the group owes them, negative when they owe it.</param>
/// <param name="IsArchive">
/// Whether they have archived it. Carried so the page can keep an archived group out of
/// the list while still counting its balance -- archiving tidies a list, it does not
/// forgive a debt.
/// </param>
public record GroupPosition(Guid GroupId, string GroupName, decimal Balance, bool IsArchive);
