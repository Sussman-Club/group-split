namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// An itemised bill, either on a seeded expense or on a seeded bank row waiting in the inbox.
/// </summary>
/// <remarks>
/// The same shape in both places, because it is the same piece of paper. On an expense it is
/// what the categories that divide by one read; on a bank row it is a charge that has not
/// been filed yet, and whose lines somebody is about to sort into one purchase or several.
/// <para>
/// The subtotal and the total are not here: they are the lines added up and then the extras
/// added on, and a seed file that stated them could disagree with its own arithmetic --
/// which the division refuses, so the seeder would fail rather than produce the wrong
/// answer. Deriving them keeps the file saying one thing.
/// </para>
/// <para>
/// What the file does have to get right is the money the bill is a bill for: the expense's
/// own <see cref="TransactionSeedDto.Amount"/>, or the bank row's
/// <see cref="BankTransactionSeedDto.Amount"/>. Those are checked rather than derived, since
/// the amount is what every other seeded expense and every other seeded row already states.
/// </para>
/// </remarks>
public class ReceiptSeedDto
{
    public decimal Tax { get; init; }

    public decimal Tip { get; init; }

    public required IReadOnlyList<ReceiptItemSeedDto> Items { get; init; }
}

/// <summary>One line on a seeded bill.</summary>
public class ReceiptItemSeedDto
{
    public required string Name { get; init; }

    /// <summary>What the line came to. The only figure the division reads.</summary>
    public required decimal Price { get; init; }

    public decimal Quantity { get; init; } = 1;

    /// <summary>
    /// What of the bill's tax was charged on this line. None unless the file says otherwise,
    /// which is what a restaurant bill under VAT wants and what every seeded dinner relies on.
    /// </summary>
    /// <remarks>
    /// These have to come to the receipt's <see cref="ReceiptSeedDto.Tax"/>, and the seed
    /// files are checked for it. It was a boolean, and the tax was then weighed over the
    /// flagged lines by price -- exact only where every taxed line carries one rate, which is
    /// not the warehouse receipt this exists for: the groceries and the clothes are taxed
    /// differently, not merely one of them and not the other.
    /// </remarks>
    public decimal Tax { get; init; }

    /// <summary>
    /// Who had it, and with what weight -- one apiece for a line shared between them, two
    /// against one for somebody who had twice as much.
    /// </summary>
    /// <remarks>
    /// Keyed by user the way <c>categories.json</c> writes shares and percentages, so the
    /// seed files say "who, how much" in one shape throughout.
    /// <para>
    /// Only on a bill whose expense is filed under an itemised rule, which is the one thing
    /// that ever reads a claim. Stated anywhere else -- under a category that divides evenly,
    /// or on a bank row still in the inbox -- the seed run stops and names the lines, rather
    /// than seed an answer to a question nothing is going to ask.
    /// </para>
    /// </remarks>
    public Dictionary<Guid, int> Had { get; init; } = [];
}
