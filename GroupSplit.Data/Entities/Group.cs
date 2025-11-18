namespace GroupSplit.Data.Entities;

public class Group : Entity
{
    public virtual ICollection<User> Users { get; } = [];
    
    public virtual ICollection<Rule> Rules { get; } = [];
    
    public virtual ICollection<Transaction> Transactions { get; } = [];
}