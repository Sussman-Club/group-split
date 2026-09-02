namespace GroupSplit.Data.Entities;

public class UserIdentity : Entity
{
    public virtual User User { get; set; } = null!;
    public string IdentityId { get; set; } = null!;
}

