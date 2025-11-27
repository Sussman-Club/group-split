
namespace GroupSplit.Shared;

public class JsonPatchDocumentOfUpdateTransactionRequest;

public record UpdateTransactionRequest
{
    public Guid PaidByUserId { get; set; }
    public Guid RuleVersionId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset DateTime { get; set; }
};