namespace GroupSplit.Seeder.Seeders.DTOs;

public class TransactionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid PayerId { get; init; }
    /// <summary>
    /// What it was filed under, or null for a personal expense -- one the payer recorded
    /// for themselves, in no group and under no category. A category belongs to a group,
    /// so naming one is also what says which group the expense is in.
    /// </summary>
    public Guid? CategoryId { get; init; }
    public required decimal Amount { get; set; }

    /// <summary>
    /// The moment it was recorded, for the history: three years of it, on the days it
    /// actually happened. Ignored when <see cref="DaysAgo"/> is set.
    /// </summary>
    public DateTimeOffset? DateTime { get; set; }

    /// <summary>
    /// Recorded this many days before the run instead, the way an imported row is dated.
    /// </summary>
    /// <remarks>
    /// For the handful of expenses that have to line up with a seeded bank row. Those rows
    /// are dated relative to the run so a database seeded months ago still reads as this
    /// week's, and an expense on a fixed date cannot be the same money as one of them for
    /// longer than a week -- which is the whole thing the pair exists to demonstrate.
    /// </remarks>
    public int? DaysAgo { get; init; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// The shop this was spent at, by name, matching an entry in <c>merchants.json</c> --
    /// or null, which is most of them.
    /// </summary>
    /// <remarks>
    /// A real expense gets this by being filed from a bank row, and demo data has no bank
    /// behind its history, so it is named here instead. Named and not seeded per expense:
    /// the point of the table is that "Coffee run" in March and "Coffee run" in August are
    /// two expenses at one place, and a name in the seed file is how the file says that.
    /// </remarks>
    public string? Merchant { get; init; }
}