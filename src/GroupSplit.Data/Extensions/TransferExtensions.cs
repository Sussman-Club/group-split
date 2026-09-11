using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Extensions;

/// <summary>
/// Making a settlement, which is the one transaction nobody may build field by field.
/// </summary>
/// <remarks>
/// The entities here are data and nothing else -- no factories, no rules, no behaviour --
/// so the one piece of construction that cannot be got wrong safely lives beside
/// <see cref="Transfer"/> rather than on it. What it guards is unchanged: a transfer's
/// single split is an invariant with no second chance, and <see cref="Transfer"/>'s
/// constructor is internal so that this remains the only way to reach one.
/// </remarks>
public static class TransferExtensions
{
    extension(Group group)
    {
        /// <summary>
        /// Money moving from one member to another, as a single row with a single split.
        /// </summary>
        /// <remarks>
        /// The one split -- to the recipient, for the whole amount -- is what makes the
        /// balances come out right: the payer's <c>paid</c> rises by the amount and the
        /// recipient's <c>owed</c> rises by it, so the payer's net goes up and the
        /// recipient's goes down, which is exactly what paying somebody back means.
        /// <para>
        /// Hung off the group because a settlement is always within one, and because the
        /// group is where the currency comes from -- a transfer in a currency its group does
        /// not use is a balance that cannot be summed.
        /// </para>
        /// </remarks>
        /// <param name="description">
        /// What the payer wants remembered about it -- "cash", "bank transfer, ref 4821".
        /// The column was always here and nothing ever set it, so a group's activity could
        /// say that Loraine paid Daniel 40 and never how.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="from"/> and <paramref name="to"/> are the same person, which is
        /// not a payment.
        /// </exception>
        public Transfer SettlementBetween(User from, User to, decimal amount, DateTimeOffset date,
            string? description = null)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(from);
            ArgumentNullException.ThrowIfNull(to);

            if (from == to)
                throw new ArgumentException("A transfer needs two different people.", nameof(to));

            var transfer = new Transfer
            {
                Group = group,
                Amount = amount,
                Currency = group.Currency,
                DateTime = date,
                Name = "Settlement",
                Description = description,
                User = from
            };

            transfer.Splits.Add(new TransactionSplit { User = to, Amount = amount });

            return transfer;
        }
    }
}
