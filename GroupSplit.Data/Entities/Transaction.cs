namespace GroupSplit.Data.Entities;

public class Transaction : Entity
{
    public virtual User User { get; set; } = null!;
    public virtual Group Group { get; set; } = null!;
    public required decimal Amount { get; set; }
}