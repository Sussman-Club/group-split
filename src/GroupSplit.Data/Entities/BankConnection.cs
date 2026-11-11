namespace GroupSplit.Data.Entities;

/// <summary>
/// One institution a person has linked, through one provider: what Plaid calls an item.
/// </summary>
/// <remarks>
/// It belongs to a person and not to a group. Imported rows are theirs until they file
/// one into a group, and no other member ever sees an account they do not own.
/// <para>
/// The provider is a column and the connector is resolved by it, so a second aggregator
/// is a second connector and a second value here; the ledger, the inbox and the UI do not
/// change. Everything the provider said that the app does not need is on the rows'
/// <see cref="BankTransaction.RawJson"/>, not on columns here.
/// </para>
/// </remarks>
public class BankConnection : Entity
{
    public virtual User User { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>
    /// Which connector speaks for this connection -- <c>"plaid"</c> today. Lower-case, and
    /// the same string the webhook route carries as its segment.
    /// </summary>
    public required string Provider { get; set; }

    /// <summary>
    /// The provider's own id for the link, which is how a webhook names it.
    /// </summary>
    public required string ProviderItemId { get; set; }

    /// <summary>
    /// For the linked-banks card. Taken from the provider at exchange and not refreshed.
    /// </summary>
    public required string InstitutionName { get; set; }

    /// <summary>
    /// The provider's access token, protected with ASP.NET Data Protection before it is
    /// written and unprotected only by the sync engine at the moment it calls the
    /// connector. Never returned by any endpoint and never logged.
    /// </summary>
    public required string AccessTokenCiphertext { get; set; }

    /// <summary>
    /// Where the last sync left off, in the provider's own terms. Null until the first
    /// sync has run, which is a real state -- "never synced" -- and not a missing value.
    /// </summary>
    public string? Cursor { get; set; }

    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Active;

    /// <summary>
    /// Set when this connection was moved onto a freshly linked item, and cleared by the
    /// first sync that completes afterwards. True means the next page run has to recognise
    /// rows it has already got under ids it has never seen.
    /// </summary>
    /// <remarks>
    /// A provider mints transaction ids per item, exactly as it mints account ids. So the
    /// re-link that <see cref="Cursor"/> is cleared for does not just re-read the history --
    /// it re-reads it under a whole new set of ids, and every row looks new to the upsert.
    /// Left alone, months of already-filed spending reappear in the inbox as fresh rows, and
    /// the duplicate check cannot warn about any of it: it compares against expenses that
    /// came from no bank row, and these all came from one.
    /// <para>
    /// One run, and only the run straight after the move. Matching stored rows by what they
    /// look like rather than by their id is the right thing exactly once -- afterwards two
    /// coffees of the same price on the same day are two rows, and treating them as one
    /// would lose a real transaction.
    /// </para>
    /// </remarks>
    public bool AccountsRekeyed { get; set; }

    /// <summary>
    /// The bank has an account this connection is not importing: one the person did not
    /// share, or shared after linking and has not shared with us yet.
    /// </summary>
    /// <remarks>
    /// Set by the provider saying so, and by a sync meeting a row on an account it does not
    /// know after asking the provider for the account list again. It is a flag and not a
    /// <see cref="BankConnectionStatus"/> because the connection is otherwise perfectly
    /// healthy -- every account it does know keeps importing -- and the person is the only
    /// one who can resolve it, through Link in update mode.
    /// <para>
    /// Cleared by a refresh, which is what that update mode ends in. A refresh that did not
    /// actually resolve it earns the flag back on the next sync that meets the row again.
    /// </para>
    /// </remarks>
    public bool AccountsNotShared { get; set; }

    /// <summary>
    /// The provider has warned that this connection will stop working soon -- a consent
    /// nearing its expiry, or an institution being migrated -- and signing in again now
    /// avoids an outage later.
    /// </summary>
    /// <remarks>
    /// Also a flag rather than a status, and for the same reason: nothing has broken yet
    /// and syncs must keep running. Cleared by the sign-in that resolves it.
    /// </remarks>
    public bool SignInExpiring { get; set; }

    public required DateTimeOffset LinkedAt { get; set; }

    /// <summary>
    /// When a sync last completed a page. Null for the same reason <see cref="Cursor"/> is.
    /// </summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    public virtual ICollection<LinkedAccount> Accounts { get; } = [];
}
