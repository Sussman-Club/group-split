namespace GroupSplit.Data.Entities;

/// <summary>
/// What one person owed on one transaction, in money.
/// </summary>
/// <remarks>
/// The single most important row in the model, because every balance is the sum of these
/// and nothing else. They are computed once, when the transaction is written or edited,
/// and stored -- so a balance is a sum over an indexed column rather than the rule
/// hierarchy re-divided in SQL on every read, and so editing a rule cannot silently
/// restate what somebody owed last March.
/// <para>
/// The splits of a transaction sum to its amount. That invariant is the whole contract:
/// if it ever fails, the group's balances do not add up, and no other check will catch
/// it.
/// </para>
/// </remarks>
public class TransactionSplit : Entity
{
    public virtual Transaction Transaction { get; set; } = null!;

    public Guid TransactionId { get; set; }

    public virtual User User { get; set; } = null!;

    /// <summary>
    /// Public, like <see cref="TransactionId"/>, because whose share this is belongs to the
    /// split rather than to how EF indexes it -- and the service that writes splits lives
    /// outside this assembly.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Their share. Negative on a refund, and zero for a member a split deliberately
    /// excludes -- both of which are worth a row, since the alternative is inferring
    /// absence from a missing one.
    /// </summary>
    public required decimal Amount { get; set; }
}
