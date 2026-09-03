using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IDebtCalculationService
{
    Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance);
}

public class DebtCalculationService(ICurrentUser currentUser) : IDebtCalculationService
{
    public async Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance)
    {
        var settlements = MinimizeTransactions(netBalance);
        var user = currentUser.User;
        if (!settlements.TryGetValue(user.Id, out var balance))
            throw new ArgumentException($"User {user.Id} not found in settlements.");

        return balance;
    }

    private static List<GroupNetBalance> FilterAndSort(
        IEnumerable<GroupNetBalance> nets,
        Func<GroupNetBalance, bool> predicate,
        bool descending)
    {
        return [..from nb in nets
                where predicate(nb)
                orderby @descending ? -nb.Balance: nb.Balance, nb.UserId
                select nb with { }];
    }

    private static Dictionary<Guid, UserGroupBalanceResponse> MinimizeTransactions(
     IEnumerable<GroupNetBalance> netBalances)
    {
        var balances = netBalances.ToList();
        
        var creditors = FilterAndSort(
            balances,
            nb => nb.Balance > 0,
            descending: true
        );

        var debtors = FilterAndSort(
            balances,
            nb => nb.Balance < 0,
            descending: false
        );

        int i = 0, j = 0;

        var result = balances.ToDictionary(
            nb => nb.UserId,
            nb => new UserGroupBalanceResponse
            {
                NetBalances = balances,
                OwedToYou = [],
                YouOwed = []
            }
        );

        // Perform the settlement
        while (i < creditors.Count && j < debtors.Count)
        {
            var creditor = creditors[i];
            var debtor = debtors[j];

            var payment = Math.Min(-debtor.Balance, creditor.Balance);

            // Creditor: someone owes them
            result[creditor.UserId].OwedToYou = result[creditor.UserId].OwedToYou
                .Append(new DebtInfo
                {
                    UserId = debtor.UserId,
                    UserName = debtor.UserName,
                    Amount = payment
                });

            // Debtor: they owe someone
            result[debtor.UserId].YouOwed = result[debtor.UserId].YouOwed
                .Append(new DebtInfo
                {
                    UserId = creditor.UserId,
                    UserName = creditor.UserName,
                    Amount = payment
                });

            // Update amounts
            debtor.Balance += payment;
            creditor.Balance -= payment;

            if (creditor.Balance == 0) i++;
            if (debtor.Balance == 0) j++;
        }

        return result;
    }
}
