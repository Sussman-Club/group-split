namespace GroupSplit.Shared;

public sealed record SupportTicketResponse(Guid Id, Guid ReporterUserId, Guid? AssignedUserId,
    string Type, string Status, string Priority, bool ClassificationNeedsReview,
    string Title, string Description, string? ReproductionSteps,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string? AssignedUserName { get; init; }
    public string? ReporterName { get; init; }
}
public sealed class UpdateSupportTicketRequest
{
    public string Type { get; set; } = "Support";
    public string Status { get; set; } = "New";
    public string Priority { get; set; } = "Normal";
    public bool ClassificationNeedsReview { get; set; }
    public bool AssignToMe { get; set; }
    public bool ChangeAssignment { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateTimeOffset ExpectedUpdatedAt { get; set; }
}
public sealed record SupportNoteResponse(Guid Id, Guid? AuthorUserId, string Body, DateTimeOffset CreatedAt);
public sealed record CreateSupportNoteRequest(string Body);
