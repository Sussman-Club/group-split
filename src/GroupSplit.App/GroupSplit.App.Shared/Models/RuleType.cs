namespace GroupSplit.App.Shared.Models;

/// <summary>
/// The kinds of division a rule can be, as the editor offers them.
/// </summary>
/// <remarks>
/// <see cref="Even"/> was missing while the only way to reach a rule was through the
/// category that owned it, and every category the app created got a rule of one of the
/// other three. The API has always had it and the CLI has always written it
/// (<c>split-rules create --even</c>), so a group could hold one -- and once the Splits tab
/// put an Edit button on every rule, opening one meant a form with no kind selected, no
/// fields, and a Save that validated to false and returned without a word.
/// </remarks>
public enum RuleType
{
    Personal,
    Even,
    Percent,
    Shares
}