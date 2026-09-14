using GroupSplit.Shared;
namespace GroupSplit.App.Shared.Services.Commands;

public interface IReceiptCommands
{
    Task<ReceiptResponse?> GetAsync(Guid transactionId, CancellationToken ct = default);
    Task<ReceiptResponse?> SetRuleAsync(Guid transactionId, Guid itemId, Guid? versionId);
    Task<ReceiptDivisionResponse?> PreviewAsync(Guid transactionId);
    Task<bool> DivideAsync(Guid transactionId);
}
