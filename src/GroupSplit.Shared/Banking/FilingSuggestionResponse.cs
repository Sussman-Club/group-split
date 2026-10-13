namespace GroupSplit.Shared;

/// <summary>
/// Where an imported row probably goes: one of the caller's own groups and one of its
/// categories.
/// </summary>
/// <remarks>
/// Only ever a preselection. Nothing is filed on the strength of it; the person still
/// presses the button, and filing with it is filing with the same group and category
/// picked by hand -- the category's rule divides it either way.
/// <para>
/// No suggestion at all is a normal answer. The model may abstain, an answer below the
/// confidence threshold counts as abstaining, a row that could already be recorded is
/// not given one, and with no model configured nothing is ever suggested.
/// </para>
/// </remarks>
/// <param name="BankTransactionId">The imported row this is about.</param>
/// <param name="GroupId">
/// The group to file it in. The caller's personal group is one of the answers, with the
/// caller's own id, and <paramref name="IsPersonal"/> says so.
/// </param>
/// <param name="Confidence">The model's confidence in this answer, from 0 to 1.</param>
public sealed record FilingSuggestionResponse(
    Guid BankTransactionId,
    Guid GroupId,
    string GroupName,
    bool IsPersonal,
    Guid CategoryId,
    string CategoryName,
    double Confidence)
{
    /// <summary>Group and category in one phrase, as a row or a button says it.</summary>
    public string Destination => $"{GroupName} · {CategoryName}";
}
