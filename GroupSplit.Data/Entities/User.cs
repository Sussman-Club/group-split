namespace GroupSplit.Data.Entities;

public class User : Entity
{
    public virtual UserIdentity Identity { get; set; } = null!;
    public virtual Group? PersonalGroup { get; set; } = null!;
    public virtual ICollection<Group> Groups { get; } = [];
    public virtual ICollection<Transaction> Transactions { get; } = [];
}