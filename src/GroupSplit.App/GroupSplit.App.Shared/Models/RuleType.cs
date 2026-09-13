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
/// <para>
/// <see cref="AllOnOnePerson"/> replaced an entry called Personal, which meant "whoever paid
/// owes all of it" -- the same division with nobody named. Two entries in a type list that
/// divide identically and differ only in whether the person is written down is a choice
/// nobody could make from the labels, so there is one, and what the form asks next is which
/// person.
/// </para>
/// </remarks>
public enum RuleType
{
    AllOnOnePerson,
    Even,
    Percent,
    Shares
}
