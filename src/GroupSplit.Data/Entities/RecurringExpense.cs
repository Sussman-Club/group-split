using GroupSplit.Shared;

namespace GroupSplit.Data.Entities;

/// <summary>
/// An expense that is created again on a schedule: the rent, a subscription.
/// </summary>
/// <remarks>
/// Each occurrence becomes an ordinary <see cref="Expense"/>, with
/// <see cref="TransactionSource.Schedule"/> as its source, and is edited as one: changing
/// this changes the occurrences still to come and none already created.
/// <para>
/// What an occurrence is lives in <see cref="Template"/> as the request it is created with,
/// not as links to the payer, category and rule. They are checked when each occurrence is
/// created, by the same code that checks an expense typed in, and a payer who has left or a
/// rule that no longer applies is reported in <see cref="Problem"/> instead of being
/// prevented here.
/// </para>
/// </remarks>
public class RecurringExpense : Entity
{
    public virtual Group Group { get; set; } = null!;
    public Guid GroupId { get; set; }

    /// <summary>
    /// Who the occurrences are entered by and checked as: whoever set it up, or last changed or
    /// resumed it.
    /// </summary>
    public virtual User EnteredBy { get; set; } = null!;
    public Guid EnteredById { get; set; }

    /// <summary>The <c>RecurringExpenseRequest</c> it was set up with, as JSON.</summary>
    public required string Template { get; set; }

    public required RepeatFrequency Frequency { get; set; }

    /// <summary>The first occurrence's day, which the later ones are counted from.</summary>
    public required DateOnly StartsOn { get; set; }

    /// <summary>
    /// The next occurrence still to be created. Moved on in the same save that creates the
    /// occurrence, and a concurrency token, so a day is created once however often the job
    /// runs.
    /// </summary>
    public required DateOnly NextOn { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>Set when it is stopped. Kept rather than deleted, like a group's past members.</summary>
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>Why the occurrence on <see cref="NextOn"/> could not be created, until it can.</summary>
    public string? Problem { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
