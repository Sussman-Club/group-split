using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Models;

/// <summary>Compatible individual suggestions, never a merchant-wide filing rule.</summary>
public sealed record InboxBundle(IReadOnlyList<BankTransactionResponse> Rows)
{
    public BankTransactionResponse First => Rows[0];
    public FilingSuggestionResponse Suggestion => First.Suggestion!;

    public static IReadOnlyList<InboxBundle> Create(IEnumerable<BankTransactionResponse> rows) =>
        [.. rows.Where(row => row.Status == InboxStatus.New && row.Amount > 0
                && !row.Pending && row.RemovedAt is null && row.PossibleDuplicates.Count == 0
                && row.AccountId is not null
                && !string.IsNullOrWhiteSpace(row.MerchantName) && row.Suggestion is not null)
            .GroupBy(row => new
            {
                Merchant = row.MerchantName!.Trim().ToUpperInvariant(),
                row.AccountId, row.Currency,
                row.PaymentChannel, row.City, row.ProviderCategoryDetailed,
                row.SpentOn.Year, row.SpentOn.Month,
                row.Suggestion!.GroupId, row.Suggestion.CategoryId, row.Suggestion.IsPersonal
            })
            .Where(group => group.Count() > 1)
            .Select(group => new InboxBundle([.. group]))];
}
