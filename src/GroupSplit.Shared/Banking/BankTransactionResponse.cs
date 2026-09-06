namespace GroupSplit.Shared;

/// <summary>
/// What has been done with an imported row, as the inbox offers it.
/// </summary>
/// <remarks>
/// Three of the four stored statuses. A superseded row -- a pending one whose posted row
/// has arrived -- is never listed and never filed, so it is not something a caller can ask
/// for or receive.
/// </remarks>
public enum InboxStatus
{
    New,
    Filed,
    Ignored
}

/// <summary>
/// Which rows the inbox should show. Defaults to what is waiting.
/// </summary>
public record InboxFilter(InboxStatus Status = InboxStatus.New);

/// <summary>
/// How many rows are waiting, for the badge in the nav.
/// </summary>
public sealed record InboxSummaryResponse(int NewCount);

/// <summary>
/// One imported row, as the inbox shows it.
/// </summary>
/// <param name="Amount">
/// Positive is money out, the same convention an expense uses. Negative is money coming in;
/// see <see cref="IsCredit"/>.
/// </param>
/// <param name="ProviderCategory">
/// The provider's own category in the provider's vocabulary, for the client to match
/// against a group's categories once a group is chosen.
/// </param>
/// <param name="TransactionId">The expense this became, once it has been filed.</param>
/// <param name="RemovedAt">
/// Set when the bank withdrew a row that had already been filed. The expense stays; this is
/// how the inbox can say what happened.
/// </param>
public sealed record BankTransactionResponse(
    Guid Id,
    DateOnly Date,
    decimal Amount,
    string Currency,
    string Description,
    string? MerchantName,
    string? ProviderCategory,
    bool Pending,
    InboxStatus Status,
    Guid? TransactionId,
    DateTimeOffset? RemovedAt,
    string AccountName,
    string InstitutionName)
{
    /// <summary>What to lead the row with: who was paid, falling back to the bank's line.</summary>
    public string Title => string.IsNullOrWhiteSpace(MerchantName) ? Description : MerchantName;

    /// <summary>
    /// Money came in rather than went out: a refund, a deposit, a paycheque. It can be
    /// ignored but not filed, because an expense is money going out and there is no other
    /// kind of transaction yet.
    /// </summary>
    public bool IsCredit => Amount < 0;

    /// <summary>
    /// The provider's category made readable: <c>FOOD_AND_DRINK</c> becomes
    /// <c>Food and drink</c>. Derived rather than stored, so it costs nothing to change.
    /// </summary>
    public string? CategoryLabel => Readable(ProviderCategory);

    private static string? Readable(string? providerCategory)
    {
        if (string.IsNullOrWhiteSpace(providerCategory))
            return null;

        var words = providerCategory.Replace('_', ' ').Trim().ToLowerInvariant();

        return words.Length == 0 ? null : char.ToUpperInvariant(words[0]) + words[1..];
    }
}

/// <summary>
/// The wire type for a page of imported rows. Named for the generator, like
/// <see cref="PagedResponseOfTransactionResponse"/>; the reasoning is there.
/// </summary>
public sealed record PagedResponseOfBankTransactionResponse(
    IReadOnlyList<BankTransactionResponse> Items,
    int Page,
    int PageSize,
    int TotalCount)
    : PagedResponse<BankTransactionResponse>(Items, Page, PageSize, TotalCount);
