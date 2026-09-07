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

    public required DateTimeOffset LinkedAt { get; set; }

    /// <summary>
    /// When a sync last completed a page. Null for the same reason <see cref="Cursor"/> is.
    /// </summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    public virtual ICollection<LinkedAccount> Accounts { get; } = [];
}
