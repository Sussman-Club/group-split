using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IDebtSettlementService
{
    Task<UserGroupBalanceResponse> GetUserSettlement(IEnumerable<GroupNetBalance> netBalance);
}

public class DebtSettlementService(IUserService userService) : IDebtSettlementService
{
    public async Task<UserGroupBalanceResponse> GetUserSettlement(IEnumerable<GroupNetBalance> netBalance)
    {
        var settlements = MinimizeTransactions(netBalance);
        var user = await userService.GetCurrentUser();
        if (!settlements.TryGetValue(user.Id, out var balance))
            throw new ArgumentException($"User {user.Id} not found in settlements.");

        return balance;
    }

    private static List<GroupNetBalance> FilterAndSort(IEnumerable<GroupNetBalance> nets, Func<GroupNetBalance, bool> predicate)
    {
        return [..from nb in nets
                where predicate(nb)
                orderby nb.Balance descending, nb.UserId
                select nb with { }];
    }

    private static Dictionary<Guid, UserGroupBalanceResponse> MinimizeTransactions(
     IEnumerable<GroupNetBalance> netBalances)
    {
        var creditors = FilterAndSort(netBalances, nb => nb.Balance > 0);
        var debtors = FilterAndSort(netBalances, nb => nb.Balance < 0);

        int i = 0, j = 0;

        var result = netBalances.ToDictionary(
            nb => nb.UserId,
            nb => new UserGroupBalanceResponse
            {
                NetBalances = netBalances.ToArray(),
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
                    UserName = debtor.UserName,
                    Amount = payment
                });

            // Debtor: they owe someone
            result[debtor.UserId].YouOwed = result[debtor.UserId].YouOwed
                .Append(new DebtInfo
                {
                    UserName = creditor.UserName,
                    Amount = payment
                });

            // Update amounts
            debtor.Balance -= payment;
            creditor.Balance -= payment;

            if (creditor.Balance == 0) i++;
            if (debtor.Balance == 0) j++;
        }

        return result;
    }
}