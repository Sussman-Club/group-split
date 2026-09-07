using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Square a group up in one action: sweep everything still outstanding -- or the part of it
/// the scope names -- and record the fewest payments that bring it to zero.
/// </summary>
/// <remarks>
/// The scope is a <see cref="TransactionFilter"/>, the same one the expense listings take,
/// because narrowing a settling-up is the same question as narrowing a list and there is no
/// reason for two vocabularies. That is also what keeps this open-ended: settling by
/// category or by payer is already expressible, and whatever the filter grows next will be
/// too, without a new endpoint or a new column.
/// </remarks>
public record SettleUpRequest
{
    /// <summary>
    /// What to settle, or null for everything outstanding -- which is the ordinary case and
    /// therefore the one that takes no arguments.
    /// </summary>
    /// <remarks>
    /// <see cref="TransactionFilter.GroupId"/> and <see cref="TransactionFilter.Personal"/>
    /// are ignored: the group is the one in the route, and a settling-up is between members
    /// of a group, so there is nothing personal to include or leave out.
    /// </remarks>
    public TransactionFilter? Scope { get; set; }

    /// <summary>
    /// What to call it -- "September", "Lisbon trip". Null takes the name the preview
    /// suggested, derived from what is actually being swept.
    /// </summary>
    [StringLength(64, ErrorMessage = "Label must be 64 characters or fewer.")]
    public string? Label { get; set; }

    /// <summary>
    /// The date the payments it writes carry. Null takes the scope's end date, and failing
    /// that, now.
    /// </summary>
    /// <remarks>
    /// A group closing September on 3 October wants those payments dated 30 September, or
    /// the month they are closing does not contain the payments that closed it. Since they
    /// have already said 30 September once -- as the end of the scope -- they do not have to
    /// say it again.
    /// </remarks>
    public DateTimeOffset? EffectiveDate { get; set; }
}
