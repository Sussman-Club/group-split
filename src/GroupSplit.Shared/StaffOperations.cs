namespace GroupSplit.Shared;

public sealed record ActivityChange(string Field, string? Before, string? After);
public sealed record StaffActivityResponse(Guid Id, string Resource, Guid ResourceId, string Action,
    string ActorName, DateTimeOffset At, List<ActivityChange> Changes);
public sealed record StaffPersonResponse(Guid Id, string Name, string? Email);
public sealed record SupportUserResponse(Guid Id, string Name, string? Email, string Status, int Groups, int OpenTickets);
public sealed record SavedStaffViewResponse(Guid Id, string Name, string Resource, string Url);
public sealed record SaveStaffViewRequest(string Name, string Resource, string Url);
public sealed record MerchantAliasResponse(Guid Id, string Name, bool IsCanonical);
public sealed record CreateMerchantAliasRequest(string Name);
public sealed record MerchantMergePreview(MerchantResponse Source, MerchantResponse Target, int Transactions, int BankRows, int Aliases);
public sealed record MergeMerchantsRequest(Guid TargetId, Guid SourceVersion, Guid TargetVersion);
public sealed record TicketSelection(Guid Id, DateTimeOffset ExpectedUpdatedAt);
public sealed class BulkTicketUpdateRequest
{
    public List<TicketSelection> Tickets { get; set; } = [];
    public string? Status { get; set; }
    public bool ChangeAssignment { get; set; }
    public Guid? AssignedUserId { get; set; }
}
public sealed record SupportMessageResponse(Guid Id, string Body, string AuthorName, bool FromReporter,
    string Visibility, DateTimeOffset CreatedAt);
public sealed class CreateSupportMessageRequest
{
    public string Body { get; set; } = "";
    public bool WaitForCustomer { get; set; } = true;
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
}
public sealed class SupportTicketQuery
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public string? Type { get; set; }
    public bool NeedsReview { get; set; }
    public Guid? AssignedUserId { get; set; }
    public bool UnassignedOnly { get; set; }
    public string Sort { get; set; } = "updated";
    public int Page { get; set; }
}
