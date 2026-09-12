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
    /// Whether the tax on the bill was charged on this line. True unless the file says
    /// otherwise, which is what a restaurant bill wants and what every seeded dinner relies
    /// on.
    /// </summary>
    /// <remarks>
    /// False is for the warehouse receipt, where the groceries are exempt and the clothes
    /// are not. It is the difference between apportioning the tax over the lines that were
    /// actually charged it and spreading it over everything -- which would tax the bananas
    /// and let the jacket off.
    /// </remarks>
    public bool Taxable { get; init; } = true;

    /// <summary>
    /// Who had it, and with what weight -- one apiece for a line shared between them, two
    /// against one for somebody who had twice as much.
    /// </summary>
    /// <remarks>
    /// Keyed by user the way <c>categories.json</c> writes shares and percentages, so the
    /// seed files say "who, how much" in one shape throughout.
    /// </remarks>
    public Dictionary<Guid, int> Had { get; init; } = [];
}
