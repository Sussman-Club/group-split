namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// An itemised bill on a seeded expense, for the categories that divide by one.
/// </summary>
/// <remarks>
/// The subtotal and the total are not here: they are the lines added up and then the extras
/// added on, and a seed file that stated them could disagree with its own arithmetic --
/// which the division refuses, so the seeder would fail rather than produce the wrong
/// answer. Deriving them keeps the file saying one thing.
/// <para>
/// The expense's own <see cref="TransactionSeedDto.Amount"/> does still have to equal that
/// total, because a bill has to be the expense's own money. That one is checked rather than
/// derived, since the amount is what every other seeded expense already states.
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
    /// Who had it, and with what weight -- one apiece for a line shared between them, two
    /// against one for somebody who had twice as much.
    /// </summary>
    /// <remarks>
    /// Keyed by user the way <c>categories.json</c> writes shares and percentages, so the
    /// seed files say "who, how much" in one shape throughout.
    /// </remarks>
    public Dictionary<Guid, int> Had { get; init; } = [];

    /// <summary>
    /// True for a line that was the table's rather than anybody's in particular, which is
    /// how a seeded bill says "these two were mine and the rest was shared" without naming
    /// four people on every other line.
    /// </summary>
    public bool Shared { get; init; }
}
