namespace GroupSplit.Data.Entities;

public sealed class StaffMember : Entity
{
    public Guid UserId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset LastVerifiedAt { get; set; }
}

public sealed class StaffActivity : Entity
{
    public Guid? ActorUserId { get; set; }
    public required string Resource { get; set; }
    public Guid ResourceId { get; set; }
    public required string Action { get; set; }
    public required string ChangesJson { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class StaffSavedView : Entity
{
    public Guid UserId { get; set; }
    public required string Resource { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SupportNotification : Entity
{
    public Guid UserId { get; set; }
    public Guid TicketId { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Canonical names and aliases share one unique namespace.</summary>
public class MerchantAlias : Entity
{
    public Guid MerchantId { get; set; }
    public virtual Merchant Merchant { get; set; } = null!;
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The old ID remains usable after the duplicate row has been consolidated.</summary>
public sealed class MerchantRedirect : Entity
{
    public Guid MerchantId { get; set; }
}
