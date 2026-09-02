using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Transactions;

public class TransactionsTracker
{
    [PersistentState] public ICollection<TransactionResponse>? Transactions { get; set; }
}