using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>A split rule as it appears in a list: enough to pick one by.</summary>
/// <param name="BuiltIn">
/// Whether the group was given this rule rather than writing it -- the one it holds per
/// member, putting the whole amount on them. Those are not renamed, restated or deleted, so
/// a client showing an Edit button beside one is offering something the API refuses.
/// </param>
/// <param name="AllForUserId">
/// The member this rule currently puts the whole amount on, or null when it divides between
/// people instead. Read off the division the rule stands for now, which is where a rule says
/// who it is for; the listing carries it because it carries no definition, and a client
/// offering "all for Ana" would otherwise have to read every rule to find Ana's.
/// </param>
/// <param name="DividesByBill">
/// Whether this is the rule that defers to the expense's own receipt, one line at a time.
/// </param>
/// <remarks>
/// Carried for the same reason as <paramref name="AllForUserId"/> and no other: a group holds
/// exactly one of these and a client offering "divide it by the bill" has to find it without
/// reading the definition of every rule the group has. It is a fact about the rule, not a
/// fact about any expense -- whether an expense actually divides by its bill is answered by
/// <c>ReceiptResponse.DividesItsExpense</c>.
/// </remarks>
public record SplitRuleResponse(
    Guid Id, Guid GroupId, string Name, bool BuiltIn = false, Guid? AllForUserId = null,
    bool DividesByBill = false);

/// <summary>A split rule with the division it stands for now.</summary>
/// <param name="VersionId">
/// The version the definition was read from -- what the rule says at this moment. An
/// expense created now records this id, and keeps it when the rule is edited afterwards, so
/// it can still be divided by exactly what divided it the first time.
/// </param>
/// <param name="ChangedAt">When the current version became what the rule says.</param>
public record SplitRuleDetailsResponse
{
    public Guid Id { get; init; }

    public Guid GroupId { get; init; }

    public string Name { get; init; } = null!;

    public Guid VersionId { get; init; }

    public DateTimeOffset ChangedAt { get; init; }

    public SplitRuleDto Definition { get; init; } = null!;

    /// <inheritdoc cref="SplitRuleResponse.BuiltIn"/>
    /// <remarks>
    /// No <c>AllForUserId</c> beside it, unlike the listing: the definition below already
    /// says who a rule is for, when it is for one person.
    /// </remarks>
    public bool BuiltIn { get; init; }
}

/// <summary>
/// One division a rule has stood for, and the window it stood for it in.
/// </summary>
/// <param name="SupersededAt">
/// When it stopped being what the rule said, or null for the one that still is.
/// </param>
public record SplitRuleVersionResponse(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? SupersededAt,
    SplitRuleDto Definition);

/// <summary>A rule's whole history, newest first.</summary>
public record SplitRuleHistoryResponse(
    Guid Id,
    Guid GroupId,
    string Name,
    IReadOnlyList<SplitRuleVersionResponse> Versions);

/// <summary>
/// One entry in a rule's history, as somebody writing that history states it.
/// </summary>
/// <param name="From">
/// When this division became what the rule said. The window it stood for runs from here to
/// the next entry's <c>From</c>, so the dates alone say how long each one lasted and there
/// is nothing to state twice.
/// </param>
/// <remarks>
/// The only place in the API where a caller sets <see cref="SplitRuleVersionResponse.StartedAt"/>.
/// Every other path stamps it from the clock, which is right for an edit somebody is making
/// now and useless for a history that happened before the rule existed here -- 42 months of
/// a workbook cannot be re-lived one save at a time.
/// </remarks>
public record SplitRuleVersionInput(DateTimeOffset From, SplitRuleDto Definition);

public record CreateSplitRuleRequest
{
    public Guid GroupId { get; init; }

    [Required(ErrorMessage = "Name is required")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; init; } = null!;

    public SplitRuleDto Definition { get; init; } = null!;
}

/// <summary>
/// What a rule should say from now on.
/// </summary>
/// <remarks>
/// Editing the definition does not overwrite anything: the version that was current is
/// closed and a new one opens, so every expense already recorded still points at the
/// division it was written under. Changing only the name changes no version at all -- the
/// name belongs to the rule, not to what it says.
/// </remarks>
public record UpdateSplitRuleRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; set; } = null!;

    public SplitRuleDto Definition { get; set; } = null!;
}
