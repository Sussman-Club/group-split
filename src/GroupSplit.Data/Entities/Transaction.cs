namespace GroupSplit.Data.Entities;

public class Transaction : Entity
{
    public virtual User User { get; set; } = null!;
    public virtual RuleVersion RuleVersion { get; set; } = null!;
    public required decimal Amount { get; set; }
    public required DateTimeOffset DateTime { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// What each person owed on this, summing to <see cref="Amount"/>.
    /// </summary>
    /// <remarks>
    /// Empty until the write paths move onto it. Nothing reads it yet: the balance is
    /// still divided out of the rule hierarchy in SQL, and this is the table it will be
    /// summed from instead.
    /// </remarks>
    public virtual ICollection<TransactionSplit> Splits { get; } = [];
}