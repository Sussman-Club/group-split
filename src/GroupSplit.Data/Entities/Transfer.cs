namespace GroupSplit.Data.Entities;

/// <summary>
/// One member paying another back.
/// </summary>
/// <remarks>
/// A settlement used to be two rows -- <c>+amount</c> against the other member and
/// <c>-amount</c> against you -- hung off a pseudo-rule that existed to be excluded from
/// things, carrying flags that existed to stop anybody recording against it. A transfer is
/// one row that goes through the same splits, the same balance query and the same delete
/// path as everything else, and needs no flags because there is nothing to forbid.
/// <para>
/// Members had already invented this: the Home group worked around the Settle button by
/// making a "Daniel pays" rule with one participant at 100% and recording repayments as
/// ordinary expenses against it. The arithmetic was right; only the modelling was missing.
/// </para>
/// </remarks>
public class Transfer : Transaction
{
    /// <summary>
    /// The last day of activity this payment was meant to settle, or null when it does not
    /// claim a period at all.
    /// </summary>
    /// <remarks>
    /// Intent, and nothing the arithmetic reads. Balances stay cumulative: a repayment counts
    /// against everything owed whatever its date and whatever this says, which is what keeps
    /// one made in September from being owed a second time by somebody settling August in
    /// October. What this adds is the answer to "what was that payment for", and the day a
    /// group's balance through can be recomputed from to say whether that period is still
    /// square -- an expense dated inside it and added later shows up as owed again, because
    /// nothing remembers which expenses were "covered".
    /// <para>
    /// A day, not an instant: "through 31 August" is a date on a calendar, and the moment it
    /// ends depends on whose calendar. Null is a real state rather than a missing value -- a
    /// payment of an amount somebody typed, or one from before the column existed, says
    /// nothing about a period, and backfilling its own date would have it claim one.
    /// </para>
    /// </remarks>
    public DateOnly? CoversThrough { get; set; }

    /// <summary>
    /// For EF, and internal so that <see cref="TransferExtensions"/> stays the only way
    /// anybody else can make one -- a transfer built field by field is a transfer whose
    /// single split somebody forgot.
    /// </summary>
    internal Transfer()
    {
    }
}
