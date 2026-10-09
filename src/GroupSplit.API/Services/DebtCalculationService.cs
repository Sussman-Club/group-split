using GroupSplit.Shared;

namespace GroupSplit.API.Services;

public interface IDebtCalculationService
{
    Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance);

    /// <summary>
    /// The caller's position through a day: what the expenses dated up to it left owing,
    /// after the repayments made for it.
    /// </summary>
    /// <remarks>
    /// <paramref name="throughDay"/> counts every settlement whatever its date, so nothing
    /// already paid is asked for twice. <paramref name="later"/> are the ones among them
    /// made for after the day -- covering through a later one, or claiming no period and
    /// dated after it. Those are taken back out and then counted again only as far as they
    /// pay down what the period left: each first between its own two ends, then whatever is
    /// left of them through other people. Money goes against the oldest
    /// debt first, so a September payment still clears what August left; but it can never
    /// make August read as a credit -- paying a September expense in September does not
    /// have August owe it back.
    /// <para>
    /// Nothing dated after the day counts otherwise. In particular a later expense never
    /// moves the figure: somebody ahead in October still owes what September left. An
    /// earlier version capped the period at the whole balance, which let October's
    /// expenses quietly shrink what September asked for. The one cap left is a later period
    /// somebody has since settled; see <see cref="CappedAt"/>.
    /// </para>
    /// </remarks>
    /// <param name="throughDay">
    /// Each member's balance counting the expenses dated up to the day and every settlement.
    /// </param>
    /// <param name="later">
    /// The settlements in <paramref name="throughDay"/> made for after the day, oldest first.
    /// </param>
    Task<UserGroupBalanceResponse> GetUserBalanceThrough(IEnumerable<GroupNetBalance> throughDay,
        IEnumerable<SettlementPayment> later);

    /// <summary>
    /// A period's position, never asking for more than a later period somebody has since
    /// settled leaves owing between the same two people.
    /// </summary>
    /// <remarks>
    /// Settling up through a later day writes the plan through that day, which can route an
    /// earlier debt through a third member: A owing C for August and C owing B for September
    /// is one payment, A to B. August on its own cannot see September, so it would still have
    /// A owe C. Once September is settled, what it leaves is the most August can still ask
    /// for -- each payment capped at the settled period's figure for that pair, and pairs it
    /// has nothing between dropped.
    /// </remarks>
    UserGroupBalanceResponse CappedAt(UserGroupBalanceResponse period, UserGroupBalanceResponse settledLater);
}

public class DebtCalculationService(ICurrentUser currentUser) : IDebtCalculationService
{
    public Task<UserGroupBalanceResponse> GetUserBalance(IEnumerable<GroupNetBalance> netBalance) =>
        Task.FromResult(Mine(MinimizeTransactions(netBalance)));

    public Task<UserGroupBalanceResponse> GetUserBalanceThrough(IEnumerable<GroupNetBalance> throughDay,
        IEnumerable<SettlementPayment> later)
    {
        var balances = throughDay.Select(balance => balance with { }).ToDictionary(balance => balance.UserId);

        // A payment with an end missing from the rows -- somebody who has since left -- stays
        // counted as it is: there is nobody on that end to weigh it against.
        var payments = later
            .Where(payment => balances.ContainsKey(payment.FromUserId) && balances.ContainsKey(payment.ToUserId))
            .ToList();

        // Out first, all of them, so the period stands on the repayments made for it alone.
        foreach (var payment in payments)
        {
            Count(balances[payment.FromUserId], paid: -payment.Amount);
            Count(balances[payment.ToUserId], received: -payment.Amount);
        }

        // Then back in, oldest first, each as far as it pays down a debt the period left
        // between its own two ends: a payer who still owed for it, to somebody still owed.
        // Its own ends first, so one person's payment is never credited to another debtor,
        // and a payment back the other way -- for something later -- never cancels it.
        var paidOut = balances.Keys.ToDictionary(userId => userId, _ => 0m);
        var paidIn = balances.Keys.ToDictionary(userId => userId, _ => 0m);

        foreach (var payment in payments)
        {
            var from = balances[payment.FromUserId];
            var to = balances[payment.ToUserId];

            var direct = Math.Min(payment.Amount, Math.Min(Math.Max(0, -from.Balance), Math.Max(0, to.Balance)));

            Count(from, paid: direct);
            Count(to, received: direct);

            paidOut[payment.FromUserId] += payment.Amount - direct;
            paidIn[payment.ToUserId] += payment.Amount - direct;
        }

        // What is left of them pays down the period through other people: C paying A and A
        // paying B clears what C owed B. Somebody who still owed for it and paid out the
        // rest since, against somebody still owed who was paid the rest since -- the same
        // total off each side, so the column still adds up.
        var owing = (from balance in balances.Values
                     where balance.Balance < 0 && paidOut[balance.UserId] > 0
                     orderby balance.Balance, balance.UserId
                     select (Balance: balance, Most: Math.Min(-balance.Balance, paidOut[balance.UserId]))).ToList();

        var owed = (from balance in balances.Values
                    where balance.Balance > 0 && paidIn[balance.UserId] > 0
                    orderby balance.Balance descending, balance.UserId
                    select (Balance: balance, Most: Math.Min(balance.Balance, paidIn[balance.UserId]))).ToList();

        var counted = Math.Min(owing.Sum(entry => entry.Most), owed.Sum(entry => entry.Most));

        var left = counted;
        foreach (var (balance, most) in owing)
        {
            var part = Math.Min(left, most);
            Count(balance, paid: part);
            left -= part;
        }

        left = counted;
        foreach (var (balance, most) in owed)
        {
            var part = Math.Min(left, most);
            Count(balance, received: part);
            left -= part;
        }

        return Task.FromResult(Mine(MinimizeTransactions(balances.Values)));
    }

    public UserGroupBalanceResponse CappedAt(UserGroupBalanceResponse period, UserGroupBalanceResponse settledLater) =>
        period with
        {
            OwedToYou = Capped(period.OwedToYou, settledLater.OwedToYou),
            YouOwed = Capped(period.YouOwed, settledLater.YouOwed),
            Plan =
            [
                .. from payment in period.Plan
                   let cap = settledLater.Plan
                       .Where(other => other.FromUserId == payment.FromUserId && other.ToUserId == payment.ToUserId)
                       .Sum(other => other.Amount)
                   where cap > 0
                   select payment with { Amount = Math.Min(payment.Amount, cap) }
            ]
        };

    private static IEnumerable<DebtInfo> Capped(IEnumerable<DebtInfo> period, IEnumerable<DebtInfo> settledLater) =>
    [
        .. from debt in period
           let cap = settledLater.Where(other => other.UserId == debt.UserId).Sum(other => other.Amount)
           where cap > 0
           select debt with { Amount = Math.Min(debt.Amount, cap) }
    ];

    /// <summary>
    /// Counts a repayment against one end of it, or takes it back out when negative: paying
    /// raises what somebody paid, being paid raises what they owe, as recording one does.
    /// </summary>
    private static void Count(GroupNetBalance balance, decimal paid = 0, decimal received = 0)
    {
        balance.AmountPaid += paid;
        balance.AmountOwed += received;
        balance.Balance += paid - received;
    }

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
