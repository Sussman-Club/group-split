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
    /// For EF, and internal so that <see cref="TransferExtensions"/> stays the only way
    /// anybody else can make one -- a transfer built field by field is a transfer whose
    /// single split somebody forgot.
    /// </summary>
    internal Transfer()
    {
    }
}
