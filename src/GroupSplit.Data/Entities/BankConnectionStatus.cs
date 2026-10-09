namespace GroupSplit.Data.Entities;

/// <summary>
/// Whether a <see cref="BankConnection"/> can still be synced.
/// </summary>
/// <remarks>
/// Stored as its name, not its number. The first enum in the data layer, and so the
/// precedent: a status column that reads <c>LoginRequired</c> in <c>psql</c> is worth more
/// than the bytes an integer saves, and it matches how the discriminators already read.
/// Renaming a member is therefore a data migration, which is the right weight for it.
/// </remarks>
public enum BankConnectionStatus
{
    /// <summary>
    /// Syncs run. The ordinary state.
    /// </summary>
    Active,

    /// <summary>
    /// The bank needs the person before it will share anything -- a changed password, a new
    /// MFA step, an expired consent, a locked account, an agreement waiting on the bank's
    /// own site, no account shared. A sync would only be told so; the way out is Link in
    /// update mode, from the linked-banks card, after whatever the bank asks for there.
    /// </summary>
    /// <remarks>
    /// One state for all of them rather than one each, because the person's way out is the
    /// same button. The provider's own code is in the log for whoever needs the detail.
    /// </remarks>
    LoginRequired,

    /// <summary>
    /// The person withdrew access at the bank's end. Nothing here can undo that; the
    /// connection is shown as such until they unlink it or link the bank afresh.
    /// </summary>
    Revoked
}
