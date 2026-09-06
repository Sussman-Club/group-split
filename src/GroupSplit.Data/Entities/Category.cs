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
    /// The split to pre-fill an expense in this category with, or null to divide it evenly
    /// between the group's members.
    /// </summary>
    /// <remarks>
    /// Pre-fill, not enforce: the amounts are copied onto the expense when it is written,
    /// so "don't charge Omar for his own birthday cake" is an edit to one expense rather
    /// than a change to how the group splits groceries forever.
    /// </remarks>
    public virtual SplitRule? DefaultSplitRule { get; set; }

    /// <summary>
    /// Public, unlike this entity's other foreign key, because which rule a category
    /// defaults to is a thing services read and write directly.
    /// </summary>
    public Guid? DefaultSplitRuleId { get; set; }
}
