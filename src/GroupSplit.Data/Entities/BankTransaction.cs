namespace GroupSplit.Data.Entities;

/// <summary>
/// A row the bank reported, as it arrived. The staging side of the seam: it is not a
/// <see cref="Transaction"/>, and it becomes one only when a person files it.
/// </summary>
/// <remarks>
/// The ledger knows nothing about banks. This row keeps everything the import needs to
/// be recognised again (<see cref="ProviderTransactionId"/>), shown (<see cref="Date"/>,
/// <see cref="Amount"/>, <see cref="MerchantName"/>), and reasoned about
/// (<see cref="Pending"/>, <see cref="ReplacesId"/>, <see cref="Status"/>); anything else
/// the provider said is in <see cref="RawJson"/> for the day it is wanted.
/// <para>
/// Filing copies, then links: the expense is built from this row's values and points back
/// here through <see cref="Transaction.BankTransactionId"/>. Editing the expense afterwards
/// never touches this row, and a later sync that modifies this row never touches the
/// expense.
/// </para>
/// </remarks>
public class BankTransaction : Entity
{
    public virtual LinkedAccount Account { get; set; } = null!;

    public Guid LinkedAccountId { get; set; }

    /// <summary>
    /// The provider's id, unique within the account. The key every upsert relies on: a
    /// page replayed after a crash finds its rows already here and changes nothing.
    /// </summary>
    public required string ProviderTransactionId { get; set; }

    /// <summary>
    /// A date and not an instant, because that is all a bank statement has. Filing turns
    /// it into midnight UTC on that day.
    /// </summary>
    public required DateOnly Date { get; set; }

    /// <summary>
    /// Positive when money left the account, which is the sign an <see cref="Expense"/>
    /// has too. Negative is money coming in -- a refund, a deposit -- and cannot be filed
    /// until there is a kind of transaction that means that.
    /// </summary>
    /// <remarks>
    /// The provider's convention is translated into this one inside its connector and
    /// nowhere else; this is the app's convention, stated once.
    /// </remarks>
    public required decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217, per row, because that is how the provider reports it.
    /// </summary>
    public string Currency { get; set; } = Currencies.Default;

    /// <summary>
    /// The line as the bank wrote it.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// The provider's cleaned-up name for who was paid, when it has one. What the inbox
    /// leads with, falling back to <see cref="Description"/>.
    /// </summary>
    public string? MerchantName { get; set; }

    /// <summary>
    /// The provider's own category for the row, in the provider's vocabulary. Turned
    /// into a readable label for the inbox; matched against a group's categories by the
    /// client, which is the only place that knows which group.
    /// </summary>
    public string? ProviderCategory { get; set; }

    /// <summary>
    /// The provider's finer category, under <see cref="ProviderCategory"/>:
    /// <c>FOOD_AND_DRINK_COFFEE</c> where the primary one says only
    /// <c>FOOD_AND_DRINK</c>. Shown, never matched on -- see
    /// <see cref="BankCategoryMapping"/> for why the mapping uses the primary one.
    /// </summary>
    public string? ProviderCategoryDetailed { get; set; }

    /// <summary>
    /// When the money was actually spent, where the provider knows it and it differs from
    /// <see cref="Date"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Date"/> is the posting date for a settled row, which can be days after
    /// the event and is not the date anybody remembers. This is the one to lead with when
    /// there is one.
    /// </remarks>
    public DateOnly? AuthorizedDate { get; set; }

    /// <summary>How it was paid: <c>in store</c>, <c>online</c>, <c>other</c>.</summary>
    public string? PaymentChannel { get; set; }

    /// <summary>Where it happened, when the provider knows. A city and nothing finer.</summary>
    public string? City { get; set; }

    /// <summary>
    /// The merchant's logo, as the provider hosts it. A row with one reads as the place it
    /// happened rather than as two initials in a circle.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Not yet settled at the bank. A pending row is not a transaction: it may change
    /// amount, or vanish, before it posts. It can still be filed, if the person wants to.
    /// </summary>
    public bool Pending { get; set; }

    /// <summary>
    /// The pending row this posted row replaced, when the provider said so. Both rows are
    /// kept; the old one is <see cref="BankTransactionStatus.Superseded"/>.
    /// </summary>
    public virtual BankTransaction? Replaces { get; set; }

    public Guid? ReplacesId { get; set; }

    public BankTransactionStatus Status { get; set; } = BankTransactionStatus.New;

    /// <summary>
    /// The expense this became, once somebody filed it. At most one, which is what the
    /// unique index on the other side already says.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="Transaction.BankTransaction"/>, and the reason the inbox
    /// can show a filed row's expense without a second query per row. It goes null rather
    /// than dangling if the expense is deleted, because deleting an expense is undoing the
    /// filing, not undoing the import.
    /// </remarks>
    public virtual Transaction? FiledAs { get; set; }

    /// <summary>
    /// When the provider withdrew a row that had already been filed. The expense stays --
    /// it is somebody's history -- and the inbox can say what happened. A row nobody had
    /// acted on is simply deleted instead, so this is set on filed rows only.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>
    /// The provider's row, verbatim. <c>jsonb</c> in Postgres, text anywhere else.
    /// </summary>
    public required string RawJson { get; set; }

    public required DateTimeOffset ImportedAt { get; set; }
}
