using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IDebtCalculationService
{
    Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance);

    /// <summary>
    /// The fewest payments that bring every balance to zero.
    /// </summary>
    /// <remarks>
    /// The same calculation the group's balances view has always shown, reachable on its
    /// own because settling up has to write exactly what that view proposes. It used to be
    /// private and immediately fanned out per member, so the one thing a settling-up needs
    /// -- the flat list of who pays whom -- had to be reassembled from the two halves it
    /// had been split into.
    /// </remarks>
    IReadOnlyList<SettlementPayment> Minimize(IEnumerable<GroupNetBalance> netBalances);
}

public class DebtCalculationService(ICurrentUser currentUser) : IDebtCalculationService
{
    public async Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance)
    {
        var settlements = PerMember(netBalance);
        var user = currentUser.User;
        if (!settlements.TryGetValue(user.Id, out var balance))
            throw new InvalidOperationException($"User {user.Id} not found in settlements.");

        return await Task.FromResult(balance);
    }

    public IReadOnlyList<SettlementPayment> Minimize(IEnumerable<GroupNetBalance> netBalances)
    {
        // Cloned, because the walk below spends each side's balance down as it matches
        // them up and the caller's rows are not ours to empty.
        var creditors = FilterAndSort(netBalances, balance => balance.Balance > 0, descending: true);
        var debtors = FilterAndSort(netBalances, balance => balance.Balance < 0, descending: false);

        var payments = new List<SettlementPayment>();

        int i = 0, j = 0;

        while (i < creditors.Count && j < debtors.Count)
        {
            var creditor = creditors[i];
            var debtor = debtors[j];

            var payment = Math.Min(-debtor.Balance, creditor.Balance);

            payments.Add(new SettlementPayment
            {
                FromUserId = debtor.UserId,
                FromUserName = debtor.UserName,
                ToUserId = creditor.UserId,
                ToUserName = creditor.UserName,
                Amount = payment
            });

            debtor.Balance += payment;
            creditor.Balance -= payment;

            if (creditor.Balance == 0) i++;
            if (debtor.Balance == 0) j++;
        }

        return payments;
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

    /// <summary>
    /// The same payments, turned round to face each member: what each of them owes and what
    /// each of them is owed.
    /// </summary>
    private Dictionary<Guid, UserGroupBalanceResponse> PerMember(IEnumerable<GroupNetBalance> netBalances)
    {
        var balances = netBalances.ToList();
        var payments = Minimize(balances);

        var result = balances.ToDictionary(
            nb => nb.UserId,
            nb => new UserGroupBalanceResponse
            {
                NetBalances = balances,
                OwedToYou = [],
                YouOwed = []
            }
        );

        foreach (var payment in payments)
        {
            result[payment.ToUserId].OwedToYou = result[payment.ToUserId].OwedToYou
                .Append(new DebtInfo
                {
                    UserId = payment.FromUserId,
                    UserName = payment.FromUserName,
                    Amount = payment.Amount
                });

            result[payment.FromUserId].YouOwed = result[payment.FromUserId].YouOwed
                .Append(new DebtInfo
                {
                    UserId = payment.ToUserId,
                    UserName = payment.ToUserName,
                    Amount = payment.Amount
                });
        }

        return result;
    }
}
