using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Settle your own position in a group in one action: record every repayment between you and
/// the rest of the group at once, instead of one dialog at a time.
/// </summary>
/// <remarks>
/// Your position, not the group's. Every payment it writes has you on one end, because what
/// you paid and what you were paid are things you were there for, and money moving between
/// two other members is not yours to record.
/// <para>
/// It settles the whole of what you owe and are owed, unless it is given a day to settle
/// through. A balance stays cumulative either way: through a day it counts the expenses
/// dated up to it and every settlement ever made, so what comes out is what is still owed
/// for that period after everything already paid -- and settling the same period twice
/// finds nothing left the second time.
/// </para>
/// </remarks>
public record SettleUpRequest
{
    /// <summary>
    /// The last day of activity to settle, or null for everything. See <see cref="Cutoff"/>.
    /// Every payment it records remembers it.
    /// </summary>
    public DateOnly? Through { get; set; }

    /// <summary>
    /// The caller's offset from UTC in minutes, which says when <see cref="Through"/> ends.
    /// </summary>
    [Range(-Cutoff.MaxOffsetMinutes, Cutoff.MaxOffsetMinutes,
        ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.UtcOffset))]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// When the money moved. Null means now.
    /// </summary>
    /// <remarks>
    /// The same field a single repayment takes, applied to all of them. A group that squares
    /// up on the 3rd for a month that ended on the 30th wants these dated the 30th, so the
    /// month they are closing contains the payments that closed it.
    /// </remarks>
    public DateTimeOffset? Date { get; set; }

    /// <summary>
    /// What to remember about it -- "end of September", "bank transfer". Written onto every
    /// payment it records, so the group's activity says which settling-up a transfer belongs
    /// to.
    /// </summary>
    /// <remarks>
    /// Optional, and deliberately so. This has to stay one tap, and a note nobody can skip is
    /// the thing that would stop it being one.
    /// </remarks>
    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionLength))]
    public string? Description { get; set; }
}
