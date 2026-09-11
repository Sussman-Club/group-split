namespace GroupSplit.Data.Entities;

/// <summary>
/// Anything with an identity of its own: one row, one id.
/// </summary>
/// <remarks>
/// The id is generated here rather than by the database, which is a decision with
/// consequences elsewhere and is worth meeting once. It lets a whole graph be built and
/// wired together in memory before anything is saved -- an expense and the splits that
/// point at it, a rule and its first version -- and it is what makes a client-supplied id,
/// as the seeder uses, an ordinary thing rather than a special path.
/// <para>
/// What it costs: a fresh entity is indistinguishable from an existing one by its key
/// alone, so EF cannot tell an insert from an update by looking. Anything attached to an
/// already-tracked parent has to say which it is -- see <c>ExpenseSplitter.Replace</c> and
/// <c>SplitRuleService.Update</c>, both of which mark the new row Added explicitly and both
/// of which were bugs before they did.
/// </para>
/// </remarks>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
