namespace GroupSplit.Shared;

public sealed record ReceiptInboxResponse(Guid Id, Guid? ExpenseId, string Currency,
    SaveReceiptRequest Receipt, IReadOnlyList<ReceiptAttachmentResponse> Attachments);

public sealed record SaveReceiptInboxRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.RegularExpression("^[A-Za-z]{3}$")]
    public string Currency { get; init; } = "USD";
    public SaveReceiptRequest? Receipt { get; init; }
}

public sealed record FileReceiptInboxRequest
{
    public Guid? ExpenseId { get; init; }
    public Guid? BankTransactionId { get; init; }
    public CreateTransactionRequest? NewExpense { get; init; }
    public FileBankTransactionRequest? BankExpense { get; init; }
    public bool CreateAnyway { get; init; }
}

public sealed record ReceiptExpenseCandidate(Guid Id, string Name, decimal Amount, string Currency, DateTimeOffset DateTime);
