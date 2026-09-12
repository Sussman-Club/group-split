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
    /// <param name="expenseId">
    /// The expense every line is the money of, or null for a bill on a bank row nobody has
    /// filed yet. Null is not an omission: which line belongs to which purchase is precisely
    /// the question an unfiled warehouse receipt has not answered, and answering it is what
    /// splitting the charge does.
    /// </param>
    public static Receipt From(ReceiptSeedDto dto, Guid? expenseId)
    {
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
