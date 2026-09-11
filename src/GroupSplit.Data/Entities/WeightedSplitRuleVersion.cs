namespace GroupSplit.Data.Entities;

/// <summary>
/// A rule that divides in proportion: each person's share is their weight over the total.
/// </summary>
/// <remarks>
/// Even, percentage and share rules are all this, differing in what a weight means and in
/// what makes one invalid -- differences their handlers carry. What they have in common is
/// the participants, so the participants are declared once, here.
/// <para>
/// A middle layer rather than a leaf, and that is the difference from what went wrong
/// before. <c>SharesRuleVersion : PercentRuleVersion</c> was a leaf inheriting a leaf, so
/// it carried its own participants <em>and</em> the ones it inherited -- the same fact
/// stored twice in two units, with a conversion between them that is where the rounding
/// bug lived. Nothing here stores anything twice, and under TPH the extra level costs no
/// table and no join.
/// </para>
/// </remarks>
public abstract class WeightedSplitRuleVersion : SplitRuleVersion
{
    /// <summary>
    /// Who the rule names, and with what weight. What a weight means is the subtype's to
    /// say -- whole shares, or hundredths of a percent, or nothing at all for an even
    /// split, which names people only to narrow who it divides between.
    /// </summary>
    public virtual ICollection<SplitRuleParticipant> Participants { get; } = [];
}
