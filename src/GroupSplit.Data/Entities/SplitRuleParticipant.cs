namespace GroupSplit.Data.Entities;

/// <summary>
/// One member's weight in one version of one split rule.
/// </summary>
/// <remarks>
/// One weight column rather than a nullable shares column beside a nullable percentage
/// one, which would allow a row with both set and no way to say which was meant. What the
/// number means is the rule's subtype to say -- whole shares, hundredths of a percent, or
/// nothing at all for an even split -- and the division treats it the same either way,
/// because a proportion is a proportion.
/// <para>
/// A weight of zero is a member the rule deliberately excludes -- "everyone but Omar" --
/// which is worth storing, because it is different from a member the rule has never heard
/// of.
/// </para>
/// </remarks>
public class SplitRuleParticipant : Entity
{
    public virtual WeightedSplitRuleVersion SplitRuleVersion { get; set; } = null!;

    public Guid SplitRuleVersionId { get; set; }

    public virtual User User { get; set; } = null!;

    /// <summary>
    /// Public, unlike the other foreign keys here, because who a rule names is the rule's
    /// data and not an artefact of how EF indexes it -- every handler reads it.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Defaulted rather than required, because an even split names people without
    /// weighting them and one apiece is what that means.
    /// </summary>
    public int Weight { get; set; } = 1;
}
