namespace GroupSplit.Shared;

public sealed class CreateSupportReportRequest
{
    public string Type { get; set; } = "Bug";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ReproductionSteps { get; set; }
}

public sealed record SupportReportResponse(Guid Id, string Type, string Status, string Title,
    string Description, string? ReproductionSteps, DateTimeOffset CreatedAt);
