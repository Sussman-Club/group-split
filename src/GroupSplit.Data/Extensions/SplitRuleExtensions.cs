using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Extensions;

/// <summary>
/// Reading a rule's chain of versions: which one it is on now.
/// </summary>
/// <remarks>
/// Beside <see cref="SplitRule"/> rather than on it, because the entities here hold data
/// and answer no questions about it. That is not only tidiness here: a computed property on
/// the entity is something EF inspects and has to be told to ignore, which is a line in the
/// model configuration to remember for every one of these. An extension member is invisible
/// to the model by construction.
/// </remarks>
public static class SplitRuleExtensions
{
    extension(SplitRule rule)
    {
        /// <summary>
        /// What the rule says now, or null when it has no open version -- which, in
        /// practice, means the versions were not loaded.
        /// </summary>
        /// <remarks>
        /// A helper over the collection rather than a second foreign key from the rule to
        /// its current version. The pointer would be the same fact stored twice -- a row
        /// that says it is current and a rule that says which row is -- with nothing but
        /// code keeping them agreeing, and it is a circular key besides, which EF can only
        /// insert by splitting one save into two.
        /// <para>
        /// <c>FirstOrDefault</c> and not <c>SingleOrDefault</c>: a second open version is a
        /// broken chain, and the database forbids it with a partial unique index, but if one
        /// ever appeared this would be read on the way to displaying a rule and throwing
        /// there turns a display into a 500. The invariant is enforced where it can be --
        /// in the schema -- and read forgivingly here.
        /// </para>
        /// </remarks>
        public SplitRuleVersion? Current =>
            rule.Versions.FirstOrDefault(version => version.SupersededAt is null);
    }
}
