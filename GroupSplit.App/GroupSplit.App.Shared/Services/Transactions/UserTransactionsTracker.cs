using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Transactions;

public class UserTransactionsTracker
{
    [PersistentState] public ICollection<TransactionResponse>? UserTransactions { get; set; }
}