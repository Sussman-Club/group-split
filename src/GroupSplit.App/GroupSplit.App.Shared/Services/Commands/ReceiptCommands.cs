using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="IReceiptCommands"/>
public sealed class ReceiptCommands(IReceiptsClient receipts) : IReceiptCommands
{
    public async Task<ReceiptResponse?> GetAsync(Guid transactionId, CancellationToken ct = default)
    {
        try
        {
            return await receipts.GetReceiptAsync(transactionId, ct);
        }
        catch (OperationCanceledException)
        {
            // The dialog that asked has closed. Rethrown rather than swallowed, so a
            // cancelled read is not mistaken for an expense that has no bill.
            throw;
        }
        catch
        {
            // Every other outcome is the same to the caller: there is nothing to show. A
            // 404 is the ordinary case -- most expenses were never itemised -- and the rest
            // are not worth interrupting somebody for, since the bill is supplementary to a
            // dialog whose subject loaded fine.
            return null;
        }
    }
}
