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
