using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Transactions;

public class TransactionsTracker
{
    [PersistentState] public PagedResponse<TransactionResponse>? Page { get; set; }

    [PersistentState] public TransactionQuery Query { get; set; } = TransactionQuery.Default;

    [PersistentState] public TransactionSummaryResponse? Summary { get; set; }

    [PersistentState] public TransactionSummaryResponse? MonthSummary { get; set; }

    [PersistentState] public TransactionSummaryResponse? MatchesSummary { get; set; }
}
