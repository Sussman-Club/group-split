namespace GroupSplit.Shared;

public sealed record ReceiptInboxResponse(Guid Id, Guid? ExpenseId, string Currency,
    SaveReceiptRequest Receipt, IReadOnlyList<ReceiptAttachmentResponse> Attachments)
{
    public Guid? MerchantId { get; init; }
    public string? MerchantLogoUrl { get; init; }
    public string? MerchantDisplayName { get; init; }
    public Guid? SuggestedDuplicateOfId { get; init; }
    public IReadOnlyList<ReceiptDuplicateCandidate> PossibleDuplicates { get; init; } = [];
}

public sealed record ReceiptDuplicateCandidate(Guid Id, Guid? ExpenseId);

public sealed record SaveReceiptInboxRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.RegularExpression("^[A-Za-z]{3}$")]
    public string Currency { get; init; } = "USD";
    public SaveReceiptRequest? Receipt { get; init; }
    public bool UpdateMerchant { get; init; }
    public Guid? MerchantId { get; init; }
}

public sealed record FileReceiptInboxRequest
{
    public Guid? ExpenseId { get; init; }
    public CreateTransactionRequest? NewExpense { get; init; }
    public bool CreateAnyway { get; init; }
}

public sealed record ReceiptExpenseCandidate(Guid Id, string Name, decimal Amount, string Currency, DateTimeOffset DateTime, bool HasReceipt = false)
{
    public string? GroupName { get; init; }
    public string? PaidByUserName { get; init; }
}
