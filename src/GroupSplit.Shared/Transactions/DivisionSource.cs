namespace GroupSplit.Shared;

/// <summary>
/// What divided one expense, stated outright.
/// </summary>
/// <param name="SplitRuleVersionId">
/// The version of the rule whose division produced the expense's shares, or null to say the
/// amounts are the expense's own -- somebody typed them, or nothing with a name divided it.
/// </param>
/// <remarks>
/// Provenance and nothing else. Not one share moves: the amounts are the record of what each
/// person owed and they stay exactly as they are, whatever this says produced them. What it
/// changes is what a later edit does -- an expense whose division is recorded as a rule's is
/// worked out again when the amount or the payer moves.
/// <para>
/// Null does not promise the amounts will never be restated. It promises that no split
/// <em>rule</em> is applied to the expense again -- but no version is read as "divided evenly,
/// under no rule", which is still a division the app made and still follows its amount. So
/// shares that happen to equal an even division are worked out again: two members holding
/// 50.00 each of 100.00 become 45.00 each when the amount is corrected to 90.00, rather than
/// earning the refusal that protects a division only a person could have arrived at. The
/// tradeoff is <c>ExpenseSplitter</c>'s and is stated there -- two divisions that agree to the
/// cent are the same division, whoever worked them out -- and it is what keeps a rename from
/// erasing the account of what divided an expense.
/// </para>
/// </remarks>
public record SetDivisionSourceRequest(Guid? SplitRuleVersionId);

/// <summary>
/// Points a group's expenses at the version of their rule that was in force on the day they
/// were spent.
/// </summary>
/// <param name="DryRun">
/// True to work out the answer and report it without saving anything, which is how a
/// 42-month back catalogue gets looked at before it is rewritten.
/// </param>
/// <remarks>
/// For a ledger imported from somewhere that had no notion of versions. The migration that
/// brought it in pointed every categorised expense at its category's only version, because
/// that was the only one there was -- so an expense from March 2023 claims to have been
/// divided by a ratio agreed in 2026. The dates are the one thing that can sort it out, and
/// they are already on the rows.
/// </remarks>
public record ReattachTransactionsRequest
{
    public Guid GroupId { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>
/// What one rule's expenses came to in a reattach.
/// </summary>
/// <param name="Uncovered">
/// How many were left pointing at nothing because no version of the rule covers their date.
/// Not a failure -- an expense older than the history that was written for its rule has no
/// honest answer -- but it is the figure that says the history does not go back far enough.
/// </param>
public record ReattachedRuleSummary(
    Guid SplitRuleId,
    string SplitRuleName,
    int Examined,
    int Changed,
    int Uncovered);

/// <summary>What a reattach did, or would do.</summary>
/// <param name="Examined">Every expense in the group, whether or not it had a rule to point at.</param>
/// <param name="Changed">How many ended up pointing somewhere other than where they started.</param>
/// <param name="LeftWithoutAVersion">
/// How many are left pointing at nothing: no category, a category naming no rule, or a date
/// no version of that rule covers.
/// </param>
public record ReattachSummaryResponse(
    Guid GroupId,
    bool DryRun,
    int Examined,
    int Changed,
    int LeftWithoutAVersion,
    IReadOnlyList<ReattachedRuleSummary> ByRule);
