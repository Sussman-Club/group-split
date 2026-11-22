using GroupSplit.App.Shared.Localization;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Extensions;

/// <summary>
/// Who recorded an expense or a repayment, said only when the row does not already say it.
/// </summary>
/// <remarks>
/// The payer is on every row, and on a repayment so is whoever was paid. When one of them
/// recorded it, "entered by" would repeat a name already there; when somebody else did, the
/// row reads as though the people it names wrote it, and they did not.
/// </remarks>
public static class EnteredByCopy
{
    /// <summary>True when neither the payer nor, on a repayment, whoever was paid recorded it.</summary>
    public static bool ByAThirdParty(Guid enteredBy, Guid paidBy, ActivityKind kind, Guid? paidTo) =>
        enteredBy != Guid.Empty && enteredBy != paidBy && (kind is not ActivityKind.Transfer || enteredBy != paidTo);

    extension(GroupActivityResponse entry)
    {
        /// <summary>
        /// <c>" · entered by Ana"</c> after a row's detail, or nothing. By name even when it is
        /// the reader, as the payer beside it is.
        /// </summary>
        public string EnteredBySuffix() =>
            ByAThirdParty(entry.EnteredByUserId, entry.PaidByUserId, entry.Kind, entry.PaidToUserId)
                ? UiText.Format($" · entered by {entry.EnteredByUserName}")
                : string.Empty;
    }
}
