namespace GroupSplit.App.Shared.Models;

public enum RuleType
{
    Personal,
    Percent,
    Shares,

    /// <summary>
    /// By the receipt on each expense: everybody owes what they claimed, plus their share of
    /// the tax and the tip.
    /// </summary>
    /// <remarks>
    /// The one kind that names nobody and weighs nothing, so the editor shows no member rows
    /// for it -- who owes what is on each expense's own bill and is not knowable when the
    /// rule is written.
    /// </remarks>
    Itemized
}