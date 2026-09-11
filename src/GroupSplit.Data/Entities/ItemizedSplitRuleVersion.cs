namespace GroupSplit.Data.Entities;

/// <summary>
/// By the bill: each person owes what they claimed, plus their share of the tax and the tip.
/// </summary>
/// <remarks>
/// Names nobody and weighs nothing, like <see cref="PayerSplitRuleVersion"/>, and extends
/// <see cref="SplitRuleVersion"/> directly for the same reason: who owes what is not a
/// proportion the rule could state. It depends on the <see cref="Receipt"/> attached to the
/// expense, which is not known when the rule is written and is different for every expense
/// filed under it.
/// <para>
/// That is the one way this differs from every other kind, and it is worth being plain about
/// rather than discovering later. A version is supposed to carry its own division, so that
/// the row an expense points at says the same thing tomorrow as it said the day the expense
/// was written -- which is what makes "divide it again by the rule it had" a query rather
/// than a guess. This version carries nothing, so what it actually promises is narrower:
/// that the expense was divided <em>by its bill</em>, and re-dividing it will consult the
/// bill as it stands. Edit the claims and an untouched expense keeps the shares it already
/// had -- those are stored -- but an edit to its amount will divide it by the new claims.
/// <see cref="Transaction.Splits"/> remain the record of what was owed; this says what
/// question produced them, and the receipt is where the answer lives.
/// </para>
/// <para>
/// A category may point at it, which is the point of it being a rule at all: "Dinners"
/// divides by the bill, so filing a restaurant expense with a receipt attached needs no
/// further instruction. An expense filed there with no receipt is refused when it is
/// divided, by name, rather than being silently split evenly.
/// </para>
/// </remarks>
public sealed class ItemizedSplitRuleVersion : SplitRuleVersion;
