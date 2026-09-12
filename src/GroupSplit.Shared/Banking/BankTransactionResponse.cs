using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// What has been done with an imported row, as the inbox offers it.
/// </summary>
/// <remarks>
/// Three of the four stored statuses. A superseded row -- a pending one whose posted row
/// has arrived -- is never listed and never filed, so it is not something a caller can ask
/// for or receive.
/// <para>
/// It travels as its name. A status that reads <c>Ignored</c> on the wire is worth more
/// than one that reads <c>2</c>, and it means reordering the members cannot silently change
/// what an older client is asking for -- the same reasoning the error codes follow, and the
/// same way these are stored.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<InboxStatus>))]
public enum InboxStatus
{
    New,
    Filed,
    Ignored
}

/// <summary>
/// Which rows the inbox should show: what has been done with them, and the days they fall
/// between. Defaults to everything waiting, whenever it is from.
/// </summary>
/// <param name="From">Inclusive first day. Null for no bound that side.</param>
/// <param name="To">Inclusive last day. Null for no bound that side.</param>
/// <remarks>
/// Days rather than instants, unlike <see cref="TransactionFilter"/>. An expense is recorded
/// at a moment somebody was somewhere; an imported row carries the calendar date its bank
/// put on it, which is not a moment and means the same day everywhere.
/// </remarks>
public record InboxFilter(
    InboxStatus Status = InboxStatus.New,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>
/// How many rows are waiting, for the badge in the nav.
/// </summary>
/// <param name="PossibleDuplicates">
/// How many of those carry a confident match -- the same money to the cent -- or null when
/// the caller did not ask. Not the rows a filing would be questioned over, which is a wider
/// set: a badge saying some of these may already be recorded should be right about it.
/// </param>
/// <remarks>
/// The duplicate count is opt-in and null by default, and the distinction matters: null is
/// "not asked", and zero is "asked, and none of them". Working it out means running the
/// matcher over every waiting row, which is far too much work for a badge that renders on
/// every page -- so the nav asks for the count alone, and the one screen that wants to say
/// "one of them may already be recorded" asks for both.
/// </remarks>
public sealed record InboxSummaryResponse(int NewCount, int? PossibleDuplicates = null);

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
/// <param name="TransactionIds">
/// The expenses this became, once it has been filed. Empty until then.
/// </param>
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
    string? ProviderCategoryDetailed,
    DateOnly? AuthorizedDate,
    string? PaymentChannel,
    string? City,
    string? LogoUrl,
    string? CategoryIconUrl,
    bool Pending,
    InboxStatus Status,
    IReadOnlyList<Guid> TransactionIds,
    DateTimeOffset? RemovedAt,
    string AccountName,
    string InstitutionName)
{
    /// <summary>
    /// Expenses already recorded that could be this same payment.
    /// </summary>
    /// <remarks>
    /// Only ever filled in for a row still waiting, and only ever a suggestion: filing it
    /// anyway is one of the two answers, and pointing it at the expense that is already
    /// there is the other. A pair somebody has said no to is not offered again.
    /// </remarks>
    public IReadOnlyList<ExpenseMatchResponse> PossibleDuplicates { get; init; } = [];

    /// <summary>
    /// How many lines the bill behind this charge has. Zero for the ordinary row, which is
    /// every charge nobody typed a bill for.
    /// </summary>
    /// <remarks>
    /// A count rather than the lines themselves, because the inbox is a list: it needs to
    /// say a charge can be broken up and offer the way in, and the screen that does the
    /// breaking up asks for the bill when it opens.
    /// <para>
    /// Not nullable, though "no bill" and "a bill with no lines" are different things. The
    /// second cannot happen -- a bill is saved with its lines and refused without them --
    /// and a nullable count would have meant relying on how a provider flattens a missing
    /// row, which the two the app runs on do not agree about.
    /// </para>
    /// </remarks>
    public int BillLineCount { get; init; }

    /// <summary>Whether this charge can be split: it has a bill, and the bill has lines.</summary>
    public bool CanSplit => BillLineCount > 1 && Status == InboxStatus.New && !IsCredit;

    /// <summary>
    /// The one expense this row became, or null when it became none -- or several.
    /// </summary>
    /// <remarks>
    /// For the ordinary filing, which is a row and an expense. Null for a split charge
    /// rather than the first of its parts: a caller following this to "the" expense would
    /// land on one part of a purchase and be told it was the whole thing.
    /// </remarks>
    public Guid? TransactionId => TransactionIds is { Count: 1 } one ? one[0] : null;

    /// <summary>What to lead the row with: who was paid, falling back to the bank's line.</summary>
    public string Title => string.IsNullOrWhiteSpace(MerchantName) ? Description : MerchantName;

    /// <summary>
    /// The mark to show for this row: the merchant's own logo, then the provider's icon for
    /// the kind of thing it was, then nothing -- which renders as initials.
    /// </summary>
    /// <remarks>
    /// Two fields and one question, answered here rather than in each client. Plaid fills
    /// the merchant logo in only for merchants it recognises and never in the sandbox, while
    /// it sends a category icon on every row, so the second is what most rows actually have.
    /// </remarks>
    public string? Mark =>
        string.IsNullOrWhiteSpace(LogoUrl)
            ? string.IsNullOrWhiteSpace(CategoryIconUrl) ? null : CategoryIconUrl
            : LogoUrl;

    /// <summary>
    /// Money came in rather than went out: a refund, a deposit, a paycheque. It can be
    /// ignored but not filed, because an expense is money going out and there is no other
    /// kind of transaction yet.
    /// </summary>
    public bool IsCredit => Amount < 0;

    /// <summary>
    /// When the money was actually spent: the authorized date where the provider knows it,
    /// otherwise the posting date.
    /// </summary>
    /// <remarks>
    /// What a row should lead with. A card charge often posts days after the event, and
    /// the posting date is not the one anybody remembers spending it on.
    /// </remarks>
    public DateOnly SpentOn => AuthorizedDate ?? Date;

    /// <summary>Where and how, in one phrase, or null when the provider said neither.</summary>
    public string? Context
    {
        get
        {
            var parts = new[] { PaymentChannel, City }.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();

            return parts.Length == 0 ? null : string.Join(", ", parts);
        }
    }

    /// <summary>The finer category made readable, for the row that wants to be specific.</summary>
    public string? DetailedCategoryLabel => Readable(ProviderCategoryDetailed);

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
