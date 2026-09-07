namespace GroupSplit.Data.Entities;

/// <summary>
/// One settling-up: a set of transactions swept together, and the transfers written to
/// bring their net to zero.
/// </summary>
/// <remarks>
/// Settling used to be one hand-typed payment at a time. A run is the whole thing at once:
/// it takes whatever is outstanding -- optionally narrowed -- works out the fewest payments
/// that square everybody, writes them, and marks the lot as swept by pointing every
/// transaction it touched at itself.
/// <para>
/// The transfers a run writes are part of what it sweeps, which is the property the
/// arithmetic rests on. Their net is exactly the negative of the net of everything else in
/// the set, so what is left outstanding after a run is what was outstanding before it minus
/// the set -- and the all-time balance every other query already computes goes on being the
/// outstanding balance, whatever scopes anybody settles by. Nothing else had to learn about
/// periods.
/// </para>
/// <para>
/// It deliberately does not store the filter it was built from. Which transactions a run
/// swept is on the transactions, so a stored filter would be provenance and nothing else --
/// and the awkward kind: a serialised shape in a column, pinned to exactly the thing that
/// has to stay free to grow. Every new way of scoping a run would become a migration and a
/// question about what old rows meant. What is worth having back -- the dates covered, how
/// many transactions, the total -- is derivable from the swept rows whenever it is asked
/// for. What is left is a name a person recognises.
/// </para>
/// </remarks>
public class SettlementRun : Entity
{
    public virtual Group Group { get; set; } = null!;

    public Guid GroupId { get; set; }

    /// <summary>
    /// What the members call it -- "September", "Lisbon trip". Defaulted from the scope so
    /// nobody has to type one, because a settling-up that demands a name before it will run
    /// is not the one-tap thing this is for.
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// Who ran it. Null once their account is gone: the run is the group's history and
    /// outlives the person who pressed the button, the same way an invitation outlives its
    /// inviter.
    /// </summary>
    public virtual User? RanBy { get; set; }

    public Guid? RanByUserId { get; set; }

    public required DateTimeOffset RanAt { get; set; }

    /// <summary>
    /// The date the transfers it wrote carry. Not the same as <see cref="RanAt"/>: a group
    /// squaring up on 3 October for September wants those payments inside September, or the
    /// month they are closing does not contain the payments that closed it.
    /// </summary>
    public required DateTimeOffset EffectiveDate { get; set; }

    /// <summary>
    /// When the claim this run made stopped being true, or null while it still holds.
    /// </summary>
    /// <remarks>
    /// A run says a set of transactions nets to zero. Editing or deleting one of them breaks
    /// that, so the claim is withdrawn rather than left standing while it is wrong: every row
    /// the run swept goes back to outstanding, and the next settling-up works the whole set
    /// out again. The payments it wrote are not undone -- that money really moved -- they
    /// simply become outstanding credits, and the recomputation counts them, so nobody is
    /// asked twice for what they have already handed over.
    /// <para>
    /// The row is kept rather than deleted. It is the group's history, and "September was
    /// settled, then reopened when the hotel bill was corrected" is a truer account of what
    /// happened than a September that was never settled at all.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ReopenedAt { get; set; }

    /// <summary>
    /// Everything this run swept: the expenses and earlier transfers it settled, and the
    /// transfers it wrote to settle them. Emptied by a reopening.
    /// </summary>
    public virtual ICollection<Transaction> Transactions { get; } = [];

    /// <summary>
    /// The transfers this run wrote, as against the ones it merely settled. Kept through a
    /// reopening, because they happened.
    /// </summary>
    public virtual ICollection<Transfer> Payments { get; } = [];
}
