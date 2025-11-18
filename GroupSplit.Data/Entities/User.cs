namespace GroupSplit.Data.Entities;

public class User : Entity
{
    public virtual ICollection<Group> Groups { get; } = [];
    public virtual ICollection<Transaction> Transactions { get; } = [];
}