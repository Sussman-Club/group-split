namespace GroupSplit.Data.Entities;

/// <summary>
/// What has been done with an imported row.
/// </summary>
/// <remarks>
/// Stored as its name, like <see cref="BankConnectionStatus"/>.
/// </remarks>
public enum BankTransactionStatus
{
    /// <summary>
    /// Arrived and waiting in the inbox.
    /// </summary>
    New,

    /// <summary>
    /// Became an expense. The expense points back through
    /// <see cref="Transaction.BankTransactionId"/>.
    /// </summary>
    Filed,

    /// <summary>
    /// The person said no. Stays ignored across every later sync, and can be restored.
    /// </summary>
    Ignored,

    /// <summary>
    /// A pending row whose posted row has arrived. The posted row carries the status this
    /// one had and points back here through <see cref="BankTransaction.ReplacesId"/>; no
    /// listing shows this one and no sync touches it again.
    /// </summary>
    Superseded
}
