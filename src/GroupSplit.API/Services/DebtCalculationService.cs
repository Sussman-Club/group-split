using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IDebtCalculationService
{
    Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance);

    /// <summary>
    /// The caller's position through a day, with every payment in it capped at what the same
    /// two people owe on the whole balance.
    /// </summary>
    /// <remarks>
    /// The balances through a day count every settlement whatever its date, so nothing
    /// already paid is asked for twice. On their own they can still ask for money nobody
    /// owes: a repayment made for a later period lands in this one as a credit -- paying a
    /// September expense in September reads, through August, as being owed it back -- and
    /// a member who has since left square is missing from the rows, so the period does not
    /// add up without them. Capping each payment at the whole plan's figure for that pair,
    /// and dropping pairs the whole plan has nothing between, means a period never records
    /// more than is owed, never the wrong way round, and never between two people who do
    /// not owe each other at all. It can ask for less than a ledger of who paid which
    /// expense would, which is the safe side to be wrong on.
    /// </remarks>
    Task<UserGroupBalanceResponse> GetUserBalanceThrough(IEnumerable<GroupNetBalance> throughDay,
        IEnumerable<GroupNetBalance> whole);
}

public class DebtCalculationService(ICurrentUser currentUser) : IDebtCalculationService
{
    public Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance) =>
        Task.FromResult(Mine(MinimizeTransactions(netBalance)));

    public Task<UserGroupBalanceResponse> GetUserBalanceThrough(IEnumerable<GroupNetBalance> throughDay,
        IEnumerable<GroupNetBalance> whole)
    {
        var period = Mine(MinimizeTransactions(throughDay));
        var all = Mine(MinimizeTransactions(whole));

        return Task.FromResult(period with
        {
            OwedToYou = Capped(period.OwedToYou, all.OwedToYou),
            YouOwed = Capped(period.YouOwed, all.YouOwed),
            Plan =
            [
                .. from payment in period.Plan
                   let cap = all.Plan
                       .Where(other => other.FromUserId == payment.FromUserId && other.ToUserId == payment.ToUserId)
                       .Sum(other => other.Amount)
                   where cap > 0
                   select payment with { Amount = Math.Min(payment.Amount, cap) }
            ]
        });
    }

    private static IEnumerable<DebtInfo> Capped(IEnumerable<DebtInfo> period, IEnumerable<DebtInfo> whole) =>
    [
        .. from debt in period
           let cap = whole.Where(other => other.UserId == debt.UserId).Sum(other => other.Amount)
           where cap > 0
           select debt with { Amount = Math.Min(debt.Amount, cap) }
    ];

    private UserGroupBalanceResponse Mine(Dictionary<Guid, UserGroupBalanceResponse> settlements)
    {
        var user = currentUser.User;
        if (!settlements.TryGetValue(user.Id, out var balance))
            throw new InvalidOperationException($"User {user.Id} not found in settlements.");

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
        // Everybody, guests included: the members record what a guest pays and is paid, so a
        // line naming one is a payment somebody can write down, and a plan that left them out
        // would not clear the group.
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

        // The plan as a whole, kept alongside the two per-person slices below. Every member
        // gets the same list: it is the group's plan, not a reading of it, and the lines a
        // given member may actually record are the ones with them on an end.
        var plan = new List<SettlementPayment>();

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

            plan.Add(new SettlementPayment
            {
                FromUserId = debtor.UserId,
                FromUserName = debtor.UserName,
                ToUserId = creditor.UserId,
                ToUserName = creditor.UserName,
                Amount = payment
            });

            // Update amounts
            debtor.Balance += payment;
            creditor.Balance -= payment;

            if (creditor.Balance == 0) i++;
            if (debtor.Balance == 0) j++;
        }

        foreach (var userId in result.Keys.ToList())
            result[userId] = result[userId] with { Plan = plan };

        return result;
    }
}
