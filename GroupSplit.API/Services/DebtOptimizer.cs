using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public static class DebtOptimizer
{
    public static GroupBalance GetUserSettlement(Guid userId, IEnumerable<GroupNetBalance> netBalance)
    {
        var settlements = MinimizeTransactions(netBalance);
        if (!settlements.TryGetValue(userId, out var balance))
            throw new ArgumentException($"User {userId} not found in settlements.");

        return balance;
    }

    private static List<(Guid UserId, decimal Amount, string UserName)> FilterAndSort(IEnumerable<GroupNetBalance> nets, Func<GroupNetBalance, bool> predicate)
    {
        return [..from nb in nets
                where predicate(nb)
                orderby nb.Balance descending, nb.UserId
                select (nb.UserId, nb.Balance, nb.UserName)];
    }

    private static Dictionary<Guid, GroupBalance> MinimizeTransactions(
     IEnumerable<GroupNetBalance> netBalances)
    {
        var creditors = FilterAndSort(netBalances, nb => nb.Balance > 0);
        var debtors = FilterAndSort(netBalances, nb => nb.Balance < 0);

        int i = 0, j = 0;

        var result = netBalances.ToDictionary(
            nb => nb.UserId,
            nb => new GroupBalance
            {
                NetBalances = netBalances.ToArray(),
                OwedToYou = [],
                YouOwed = []
            }
        );

        // Perform the settlement
        while (i < creditors.Count && j < debtors.Count)
        {
            var (credUser, credAmt, credName) = creditors[i];
            var (debtUser, debtAmt, debtName) = debtors[j];
            debtAmt = -debtAmt; // Make positive for easier calculations

            var payment = Math.Min(credAmt, debtAmt);

            // Add to creditor: someone owes them
            result[credUser].OwedToYou = result[credUser].OwedToYou
                .Append(new DebtInfo
                {
                    UserName = credName,
                    Amount = payment
                });

            // Add to debtor: they owe someone
            result[debtUser].YouOwed = result[debtUser].YouOwed
                .Append(new DebtInfo
                {
                    UserName = debtName,
                    Amount = payment
                });

            // Update amounts
            credAmt -= payment;
            debtAmt -= payment;

            if (credAmt == 0) i++;
            if (debtAmt == 0) j++;
        }

        return result;
    }
}