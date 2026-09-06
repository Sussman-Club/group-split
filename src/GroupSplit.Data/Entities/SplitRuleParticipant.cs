namespace GroupSplit.Data.Entities;

/// <summary>
/// One member's weight in one split rule.
/// </summary>
/// <remarks>
/// One weight column rather than a nullable shares column beside a nullable percentage
/// one, which would allow a row with both set and no way to say which was meant. What the
/// number means is <see cref="SplitRule.Kind"/>'s to say, and the division treats it the
/// same either way.
/// <para>
/// A weight of zero is a member the rule deliberately excludes -- "everyone but Omar" --
/// which is worth storing, because it is different from a member the rule has never heard
/// of.
/// </para>
/// </remarks>
public class SplitRuleParticipant : Entity
{
    public virtual SplitRule SplitRule { get; set; } = null!;

    internal Guid SplitRuleId { get; set; }

    public virtual User User { get; set; } = null!;

    internal Guid UserId { get; set; }

    public required int Weight { get; set; }
}
