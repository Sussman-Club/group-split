namespace GroupSplit.Data.Entities;

public enum SupportTicketType { Bug, FeatureRequest, Support }
public enum SupportTicketStatus { New, UnderReview, InProgress, Resolved, Closed }
public enum SupportTicketPriority { Low, Normal, High, Urgent }
public enum SupportMessageVisibility { Public, Internal }

/// <summary>A report owned by the application; external issues are only references.</summary>
public class SupportTicket : Entity
{
    public Guid ReporterUserId { get; set; }
    public Guid? AssignedUserId { get; set; }
    public SupportTicketType Type { get; set; }
    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.New;
    public SupportTicketPriority Priority { get; set; } = SupportTicketPriority.Normal;
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? ReproductionSteps { get; set; }
    public string? AppVersion { get; set; }
    // Only explicitly consented, bounded device details; never raw logs, URLs or tokens.
    public bool DiagnosticsConsent { get; set; }
    public string? Platform { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? BrowserVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? DuplicateOfTicketId { get; set; }
    public string? GitHubRepository { get; set; }
    public int? GitHubIssueNumber { get; set; }
}

public class SupportTicketMessage : Entity
{
    public Guid TicketId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public SupportMessageVisibility Visibility { get; set; } = SupportMessageVisibility.Public;
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}

/// <summary>Metadata only. ObjectKey is a private storage key, not a public URL.</summary>
public class SupportTicketAttachment : Entity
{
    public Guid TicketId { get; set; }
    public Guid? UploadedByUserId { get; set; }
    public required string ObjectKey { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Length { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

public class SupportTicketStatusChange : Entity
{
    public Guid TicketId { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public SupportTicketStatus? FromStatus { get; set; }
    public SupportTicketStatus ToStatus { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
