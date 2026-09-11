namespace GroupSplit.Shared;

/// <summary>
/// What an expense would be divided into if it were saved as described -- worked out by the
/// same code that would save it, and saving nothing.
/// </summary>
/// <remarks>
/// The dialog could divide evenly itself, and used to, in a starting point that was allowed
/// to be approximate because a person was about to edit it. A preview cannot be
/// approximate: it is shown as what will happen, so anything the client re-derives is a
/// second copy of the money arithmetic that has to agree with the first to the cent. This
/// asks instead.
/// </remarks>
/// <param name="RuleName">
/// The rule whose version actually divided it, or null when none did -- an even division,
/// or shares the caller stated. Named so the dialog can say why the numbers are what they
/// are.
/// <para>
/// Read off the version the splitter settled on rather than off the category, which is a
/// difference on two paths: shares somebody typed have no rule behind them and used to be
/// reported under the category's, and an edit divides by the version the expense was
/// written under, which may belong to a rule the category no longer names.
/// </para>
/// </param>
/// <param name="RuleSupersededAt">
/// When the version that divided it stopped being what the rule says, or null when it still
/// is. Non-null on an edit whose rule has changed since -- the expense is divided again by
/// the version it had, and this is the dialog's chance to say so rather than leave somebody
/// wondering why the numbers do not match the rule they just edited.
/// </param>
public record SplitPreviewResponse(
    IReadOnlyList<TransactionSplitResponse> Splits,
    string? RuleName,
    DateTimeOffset? RuleSupersededAt = null);
