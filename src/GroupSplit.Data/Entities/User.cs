namespace GroupSplit.Data.Entities;

public class User : Entity
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    
    public virtual UserIdentity Identity { get; set; } = null!;
    public virtual Group PersonalGroup { get; set; } = null!;
    public virtual ICollection<Group> Groups { get; } = [];
    public virtual ICollection<Transaction> Transactions { get; } = [];
}