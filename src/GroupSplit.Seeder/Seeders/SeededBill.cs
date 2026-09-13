using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// A seeded bill, built from the few figures the seed file states and with the rest worked
/// out from them.
/// </summary>
/// <remarks>
/// Shared by the two seeders that can carry one, because there are two ways a bill arrives
/// in the app and a demo wants both: typed straight onto an expense somebody recorded, or
/// typed against an imported bank row and left in the inbox to become one expense or several.
/// <para>
/// The subtotal is the lines added up and the total is that plus the extras, so a seed file
/// cannot state a receipt that disagrees with itself. What it can still state is one that
/// disagrees with the money it is a bill for, and that is left to fail: dividing an expense
/// and splitting a charge both refuse a bill whose total is not the sum being divided, by
/// name and with both figures, which is a better thing for a seed run to say than a balance
/// nobody checked.
/// </para>
/// </remarks>
internal static class SeededBill
{
    /// <summary>The bill on a seeded expense.</summary>
    /// <param name="dividedByIt">
    /// Whether the expense is filed under a rule that divides by its bill -- which is the
    /// only thing that ever reads who had what. See <see cref="Build"/>.
    /// </param>
    public static Receipt ForExpense(ReceiptSeedDto dto, Guid expenseId, bool dividedByIt) =>
        Build(dto, expenseId, claimsAreRead: dividedByIt,
            $"expense {expenseId}, which is not split by its bill");

    /// <summary>The bill on an imported charge nobody has filed yet.</summary>
    /// <remarks>
    /// Its lines belong to no expense, which is not an omission: which line belongs to which
    /// purchase is precisely the question an unfiled warehouse receipt has not answered, and
    /// answering it is what filing or splitting the charge does. So it carries no claims
    /// either -- there is no expense to divide, no rule to divide it by, and no group whose
    /// members a claim could name.
    /// </remarks>
    public static Receipt ForBankRow(ReceiptSeedDto dto) =>
        Build(dto, expenseId: null, claimsAreRead: false, "a bank row nobody has filed");

    /// <param name="claimsAreRead">Whether anything will ever look at who had what.</param>
    /// <param name="where">
    /// The bill's place, for the refusal to name. The caller's own words, because only the
    /// caller knows whether this is an expense that divides some other way or a charge with
    /// no expense at all.
    /// </param>
    /// <remarks>
    /// Exactly one thing reads a claim: the itemised rule, dividing the expense whose lines
    /// these are. A bill under a category that divides evenly, and a bill on a charge still
    /// in the inbox, would store their claims and never be asked about them.
    /// <para>
    /// Refused rather than seeded, and refused rather than quietly dropped. A claim nothing
    /// reads is a statement the demo makes and the app does not: it shows "who had it" beside
    /// a line whose share is worked out from something else entirely, and it puts the screens
    /// that draw a bill in the position of deciding whether to nag about the lines that have
    /// none. Dropping it silently would leave the seed file going on saying it.
    /// </para>
    /// </remarks>
    private static Receipt Build(
        ReceiptSeedDto dto, Guid? expenseId, bool claimsAreRead, string where)
    {
        if (!claimsAreRead && dto.Items.Any(line => line.Had.Count > 0))
        {
            var named = dto.Items.Where(line => line.Had.Count > 0).Select(line => line.Name);

            throw new InvalidOperationException(
                $"Seeded bill for {where} says who had {string.Join(", ", named)}. Only a bill " +
                "on an expense split by its bill is ever asked who had what, so take the claims " +
                "off it, or file it under a category whose rule is itemised.");
        }

        var subtotal = dto.Items.Sum(line => line.Price);

        var receipt = new Receipt
        {
            Subtotal = subtotal,
            Tax = dto.Tax,
            Tip = dto.Tip,
            Total = subtotal + dto.Tax + dto.Tip
        };

        var position = 0;

        foreach (var line in dto.Items)
        {
            var item = new ReceiptItem
            {
                // The order the seed file lists them in, which is the order they are on the
                // paper -- and what makes a demo bill's line numbers mean anything.
                Position = position++,
                Name = line.Name,
                NormalizedName = line.Name.Trim().ToLowerInvariant(),
                TotalPrice = line.Price,
                Quantity = line.Quantity,
                UnitPrice = line.Quantity == 0
                    ? line.Price
                    : decimal.Round(line.Price / line.Quantity, 2),
                IsTaxable = line.Taxable,
                ExpenseId = expenseId
            };

            foreach (var (userId, weight) in line.Had)
                item.Claims.Add(new ReceiptItemClaim { UserId = userId, Weight = weight });

            receipt.Items.Add(item);
        }

        return receipt;
    }
}
