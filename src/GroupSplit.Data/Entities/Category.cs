namespace GroupSplit.Data.Entities;

/// <summary>
/// What an expense was for -- "Groceries", "Utilities" -- and, optionally, how the group
/// usually divides that kind of expense.
/// </summary>
/// <remarks>
/// The relationship between a label and a split used to run the other way: the rule was
/// the category, so a category with no rule could record nothing, and two categories that
/// happened to divide the same way had to be two identical rules. Pointing the label at
/// the rule instead means one rule can serve Groceries, Utilities and Cleaning at once,
/// and a category with no rule at all is a perfectly ordinary thing -- it divides evenly.
/// </remarks>
public class Category : Entity
{
    public virtual Group Group { get; set; } = null!;

    internal Guid GroupId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The rule to pre-fill an expense in this category with, or null to divide it evenly
    /// between the group's members.
    /// </summary>
    /// <remarks>
    /// The rule and not one of its versions. A category names a division the way a person
    /// does -- "however we split rent" -- and follows it when it is edited; the expense
    /// records which version it actually got, which is where history lives. So the two edits
    /// are independent: re-pointing a category changes nothing about the rule, and editing
    /// the rule changes nothing about which categories point at it.
    /// <para>
    /// Pre-fill, not enforce: the amounts are copied onto the expense when it is written, so
    /// "don't charge Omar for his own birthday cake" is an edit to one expense rather than a
    /// change to how the group splits groceries forever.
    /// </para>
    /// </remarks>
    public virtual SplitRule? DefaultSplitRule { get; set; }

    /// <summary>
    /// Public, unlike this entity's other foreign key, because which rule a category
    /// defaults to is a thing services read and write directly.
    /// </summary>
    public Guid? DefaultSplitRuleId { get; set; }

    /// <summary>
    /// When the group stopped filing under this, or null while it is still in use.
    /// </summary>
    /// <remarks>
    /// What a group actually wants when it says "delete this category" is usually to stop
    /// being offered it -- and deleting is refused outright once anything is filed under it,
    /// because a category is how a group's spending is read back and dropping one would take
    /// months of that with it. Archiving is the answer to what was being asked: the category
    /// leaves the pickers and the lists, and every expense filed under it goes on naming it.
    /// <para>
    /// A timestamp rather than a flag, matching <see cref="GroupMembership.ArchivedAt"/>.
    /// Null is the whole of "not archived", and when it is set it is worth knowing since
    /// when -- a category retired last year and one retired this morning are different
    /// things to somebody deciding whether to bring it back.
    /// </para>
    /// <para>
    /// Unlike a group's, this is not personal. A group's categories are the group's, so one
    /// member retiring one retires it for everybody; archiving a group is one person tidying
    /// their own list, which is why that one hangs off the membership.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ArchivedAt { get; set; }
}
