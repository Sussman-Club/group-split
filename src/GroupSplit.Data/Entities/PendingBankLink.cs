namespace GroupSplit.Data.Entities;

/// <summary>
/// A link that has been started with a provider and not yet stored as a
/// <see cref="BankConnection"/>.
/// </summary>
/// <remarks>
/// The provider's item exists from the moment somebody finishes linking, which is before
/// this application hears anything about it, and the token that names it is handed over
/// exactly once. So the window between hearing about it and having stored it is the
/// expensive one: anything that goes wrong in there -- a database that is briefly gone, a
/// process that is killed mid-request -- drops the only token that could ever have named
/// that item again. It is then live at the provider, counted against whatever the plan
/// counts, and can be neither synced, nor repaired, nor removed.
/// <para>
/// This row is the token written down before it can be lost. It is created from the public
/// token as the request arrives, carries the exchanged item a moment later, and is deleted
/// once a connection exists. Anything left behind is a link that was interrupted, and can
/// be finished later rather than started again -- which matters because starting again
/// spends another of the provider's items, and finishing does not.
/// </para>
/// <para>
/// Both tokens are protected the same way a connection's is, by the same key ring: this row
/// holds bank access for as long as it exists, and the public token is only briefly less
/// valuable than the access token that replaces it.
/// </para>
/// </remarks>
public class PendingBankLink : Entity
{
    public virtual User User { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>Which connector this was started with; the same string a connection carries.</summary>
    public required string Provider { get; set; }

    /// <summary>
    /// The provider's one-time token, protected. Cleared once it has been exchanged, because
    /// it is spent at that point and keeping a spent credential is not free.
    /// </summary>
    /// <remarks>
    /// Worth writing down for the few minutes it lasts: while it is alive an interrupted
    /// link can still be completed without asking anybody to link their bank again.
    /// </remarks>
    public string? PublicTokenCiphertext { get; set; }

    /// <summary>
    /// The exchanged item -- its id, institution, accounts and access token -- as protected
    /// JSON. Null until the exchange has happened.
    /// </summary>
    /// <remarks>
    /// The whole item rather than a column each, because what the retry needs is exactly
    /// what the exchange returned, and a provider adding a field to it should not be a
    /// migration here. It is read by the same code that wrote it, in the same deployment,
    /// and it never outlives the connection it becomes.
    /// </remarks>
    public string? ItemCiphertext { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// How many times finishing this has been tried. Bounded, so a link that cannot be
    /// finished stops being retried rather than being retried forever.
    /// </summary>
    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }
}
