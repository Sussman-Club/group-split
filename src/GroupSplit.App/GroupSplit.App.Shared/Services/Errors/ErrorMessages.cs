using GroupSplit.App.Shared.Extensions;
using GroupSplit.Shared.Errors;

namespace GroupSplit.App.Shared.Services.Errors;

/// <summary>
/// The one table that turns an error code into something a person can read. The API's
/// <c>title</c> and <c>detail</c> are written for developers and may change; these are
/// written for the people using the app and are ours to word. A code missing from here
/// falls back to a message for its status, so a newer API never produces a blank.
/// </summary>
public static class ErrorMessages
{
    public const string Network = "We could not reach the server. Check your connection and try again.";

    public const string SessionExpired = "Your session has expired. Please sign in again.";

    public const string Generic = "Something went wrong. Please try again.";

    private const string ServerPrefix = "Something went wrong on our side. Please try again in a moment.";

    /// <summary>
    /// The 5xx message carries the trace id: it is the one thing that lets whoever reads a
    /// bug report find the log line, and the person seeing it has nothing else to quote.
    /// </summary>
    public static string Server(string? traceId) =>
        string.IsNullOrEmpty(traceId) ? ServerPrefix : $"{ServerPrefix} (Reference {traceId})";

    private static readonly Dictionary<string, string> ByCode = new(StringComparer.Ordinal)
    {
        [ErrorCodes.BadRequest] = "The request could not be understood. Please try again.",
        [ErrorCodes.ValidationFailed] = "Some of what you entered is not valid.",
        [ErrorCodes.Unauthenticated] = SessionExpired,
        [ErrorCodes.Forbidden] = "You are not allowed to do that.",
        [ErrorCodes.NotFound] = "We could not find what you were looking for.",
        [ErrorCodes.Conflict] = "That is not possible right now.",
        [ErrorCodes.InternalError] = ServerPrefix,

        [ErrorCodes.GroupNotFound] = "That group no longer exists.",
        [ErrorCodes.UserNotFound] = "That person could not be found.",
        [ErrorCodes.AccountNotFound] = "Your account could not be found.",
        [ErrorCodes.TransactionNotFound] = "That expense no longer exists.",
        [ErrorCodes.CategoryNotFound] = "That category no longer exists.",
        [ErrorCodes.MerchantNotFound] = "That merchant no longer exists.",
        [ErrorCodes.SplitRuleNotFound] = "That split rule no longer exists.",

        // A bill is the exception rather than the rule, so this reads as a statement about
        // the expense rather than as something having gone missing.
        [ErrorCodes.ReceiptNotFound] = "That expense has no itemised bill on it.",
        [ErrorCodes.ReceiptItemNotFound] = "That line is no longer on the bill.",
        [ErrorCodes.ReceiptAttachmentNotFound] = "That receipt file is no longer attached to this expense.",
        [ErrorCodes.ReceiptAttachmentInvalid] = "Choose a JPG, PNG, WebP, or PDF file no larger than 10 MB.",
        [ErrorCodes.ReceiptTranscriptionUnavailable] = "Receipt transcription is not switched on for this app yet.",
        [ErrorCodes.ReceiptTranscriptionProviderUnavailable] = "We could not read that receipt just now. Please try again in a few minutes.",
        [ErrorCodes.ReceiptInvalid] = "That bill could not be read. Check the lines and who is on them.",
        [ErrorCodes.ReceiptDoesNotAddUp] = "The bill does not add up to what was paid.",

        // The one refusal somebody can act on directly, so it says what to do rather than
        // what went wrong.
        [ErrorCodes.ReceiptItemsMissingRule] = "Some items need a split rule. "
                                             + "Choose a rule for each item before dividing it.",

        [ErrorCodes.GroupCannotRemoveSelf] = "You cannot remove yourself from a group. Leave it instead.",
        // A guest's link is what reaches this now, and it most often means somebody already
        // used it or a member stopped it -- not that the URL was mistyped.
        [ErrorCodes.GroupInvitationNotFound] = "That link no longer works. Ask someone in the group for a new one.",
        // Somebody else on the People tab got there first: removed the guest, or the guest's
        // link was claimed and they are a member now. Either way the row is out of date.
        [ErrorCodes.GroupGuestNotFound] =
            "That guest is no longer in the group. Someone may have removed them, or taken their place with their link.",
        [ErrorCodes.GroupJoinLinkNotFound] = "That join link is not one of ours. Check you copied the whole of it.",
        [ErrorCodes.GroupJoinLinkExpired] = "That join link has expired. Ask someone in the group for a new one.",
        [ErrorCodes.GroupJoinLinkRevoked] = "That join link has been withdrawn. Ask someone in the group for a new one.",
        [ErrorCodes.GroupRejoinNeedsNewInvitation] =
            "You are no longer in this group, and this link was shared before you left. " +
            "Ask someone in the group for a new one.",
        [ErrorCodes.GroupCannotLeaveLastMember] = "You are the only member left. Archive the group instead of leaving it.",
        [ErrorCodes.GroupInvitationNoName] = "A guest needs a name.",
        // GROUP_MEMBER_NOT_JOINED and SETTLEMENT_WITH_PENDING_INVITEE had messages here. Both
        // are retired -- guests are removed and settled with like anybody else -- and the
        // API never sends them, so a sentence for them would only be a promise about a
        // refusal that cannot happen.
        [ErrorCodes.TransactionGroupLeft] = "You are no longer in this transaction's group, so it cannot be changed.",
        [ErrorCodes.TransactionMovesDepartedBalance] =
            "This would change what somebody who has left the group paid or owed here. They settled up " +
            "before leaving, so their part stays as it is. You can still change the name, note, date or " +
            "category. If they only had a share, you can also set the other shares yourself and leave " +
            "theirs unchanged; if they paid for it, the amount stays too.",

        // One refusal for three acts: removing a member, removing a guest, and leaving. So it
        // names nobody and says the rule they all share; removing somebody from the People
        // tab says who and how much instead, from the balance the refusal carries -- see
        // NotSquare below.
        // Somebody else changed the same guest a moment earlier; nothing was written. The
        // People tab re-reads on the change announcement, so looking again is enough.
        [ErrorCodes.GroupGuestChanged] =
            "Someone else changed this guest at the same moment, so nothing was done. Look again and try once more.",
        [ErrorCodes.GroupMemberNotSettled] =
            "Nobody can leave or be removed while they still owe or are owed money in this group. Settle up first.",
        [ErrorCodes.AccountNotSettled] = "Settle up in every group before deleting your account.",
        [ErrorCodes.TransactionPayerNotInGroup] =
            "The person who paid is neither a member nor a guest of this group.",
        [ErrorCodes.SplitUserNotInGroup] =
            "One of the shares names someone who is neither a member nor a guest of this group.",
        [ErrorCodes.SettlementWithSelf] = "You cannot settle up with yourself.",
        [ErrorCodes.SettlementNothingToSettle] = "You are already square with everybody in this group.",
        [ErrorCodes.CategoryNameTaken] = "This group already has a category with that name.",
        // Points at the thing they almost certainly meant. Moving a year of expenses out of
        // a category so that it can be deleted is nobody's actual goal; not being offered it
        // any more is, and archiving does that without touching what is filed under it.
        [ErrorCodes.CategoryInUse] =
            "This category still has expenses filed under it. Archive it instead to stop being offered it.",
        [ErrorCodes.SplitRuleNameTaken] = "This group already has a rule with that name.",
        [ErrorCodes.SplitRuleInUse] = "This split is in use. A category still points at it, or an expense was divided by it.",
        // The first two reach a person only through the CLI: writing a rule's past wholesale
        // is a migration tool, not a dialog. The table is complete by policy anyway, and the
        // fallback it would otherwise give is the flat "That is not possible right now".
        // The third is reachable from the app, where the edit dialog offers to correct which
        // version divided an expense.
        [ErrorCodes.SplitRuleAlreadyHasHistory] =
            "This rule has already changed at least once, so its past cannot be written in wholesale.",
        [ErrorCodes.SplitRuleHistoryEndsElsewhere] =
            "The last entry is not how this rule divides today. Correct the rule first, then write its past.",
        [ErrorCodes.SplitRuleVersionNotInGroup] =
            "That division belongs to another group, so it cannot be the one behind this expense.",
        // Reachable from the app only by a screen that offered the button it should not
        // have, so it says what to do instead rather than only what went wrong.
        [ErrorCodes.SplitRuleNotEditable] =
            "This is a rule the group was given rather than wrote, so it cannot be changed. "
            + "Create a rule of your own to divide differently.",
        // Not reachable from the app, which stopped offering "by the bill" as a kind somebody
        // writes once every group was given one. Carried because the CLI can still ask.
        [ErrorCodes.SplitRuleBillIsProvisioned] =
            "This group already has its rule for dividing an expense by its own bill, and a "
            + "second would divide exactly the same way.",
        // The app does not offer the change -- the editor fixes the kind on an existing rule
        // -- so this reaches somebody only through a client that did. It still says what to
        // do, because "you cannot" without "instead" is the worst kind of refusal.
        [ErrorCodes.SplitRuleKindFixed] =
            "A rule keeps the shape it was written with. Create a rule for the new one.",
        [ErrorCodes.SplitRuleNotInGroup] =
            "That split rule belongs to another group, so it cannot divide this expense.",
        // "Already" and not "this group already": a merchant is shared, so the one it
        // collides with may well be a place somebody else's bank reported. Both retired --
        // the API no longer sends them -- and kept for a client talking to an older one.
        [ErrorCodes.MerchantNameTaken] = "There is already a merchant with that name.",
        [ErrorCodes.MerchantInUse] = "This merchant still has transactions pointing at it. Move them first.",
        // The app offers none of these edits, so this reaches somebody only through a
        // client that did. Says why, since "not allowed" alone reads like a permissions bug.
        [ErrorCodes.MerchantEditForbidden] =
            "Places are shared with everyone who has spent there, so they cannot be renamed or deleted.",
        [ErrorCodes.MerchantLogoNotAllowed] = "A logo can only come from your bank's provider.",
        [ErrorCodes.MerchantSearchTooShort] = "Type at least two letters to search.",

        [ErrorCodes.SplitRuleInvalid] = "The split does not add up. Check it and try again.",
        [ErrorCodes.SplitsInvalid] =
            "The shares are not valid. Each person can appear only once, and every share goes the same way as the amount.",
        [ErrorCodes.PatchInvalid] = "That change could not be applied. Reload and try again.",
        [ErrorCodes.SettlementAmountNotPositive] = "A settlement has to be for more than zero.",
        [ErrorCodes.NotASettlement] = "Only a settlement covers a period. An expense has no day it settles through.",
        [ErrorCodes.SplitOnAPersonalExpense] = "A personal expense is not shared with anybody, so it cannot be split.",
        [ErrorCodes.SplitsDoNotSumToAmount] = "The shares have to add up to the amount of the expense.",
        [ErrorCodes.RuleUsersNotInGroup] = "The rule names someone who is not in the group.",
        [ErrorCodes.SplitRuleHistoryInvalid] =
            "The entries do not describe a history: each one has to start after the one before it.",

        [ErrorCodes.BankConnectionNotFound] = "That linked bank no longer exists.",
        [ErrorCodes.BankTransactionNotFound] = "That imported transaction is no longer in your inbox.",
        [ErrorCodes.BankSyncUnavailable] = "Bank sync is not switched on for this app yet.",
        [ErrorCodes.BankTransactionAlreadyFiled] = "This one has already been added.",
        [ErrorCodes.BankConnectionNeedsAttention] = "Your bank needs you to sign in again before this can be synced.",
        [ErrorCodes.BankTransactionIsCredit] = "This is money coming in, so it cannot be added as an expense. You can ignore it instead.",
        [ErrorCodes.BankProviderUnavailable] = "We could not reach your bank just now. Please try again in a few minutes.",
        [ErrorCodes.BankConnectionUnrecoverable] = "We can no longer read the access your bank granted for this connection, so signing in again cannot repair it. Remove it and link the bank again.",
        [ErrorCodes.BankLinkNotSaved] = "Your bank approved the connection, but something went wrong here before we could save it, so we handed the access straight back. Nothing is linked. Please try again.",
        [ErrorCodes.BankLinkNotSavedAccessRemains] = "Your bank approved the connection, but something went wrong here before we could save it -- and we could not hand the access back. Nothing is linked here. Please try again, and you can withdraw this app's access from your bank if you would rather not.",
        [ErrorCodes.BankLinkWillBeFinished] = "Your bank approved the connection and we have it safely, but we could not finish setting it up just now. It will appear on its own shortly -- there is nothing you need to do, and no need to link it again.",
        [ErrorCodes.CurrencyMismatch] = "This is in a different currency from the group, and we cannot convert it yet. You can keep it personal instead.",
        [ErrorCodes.PossibleDuplicateExpense] = "This looks like an expense you have already recorded. Check the suggestion on the row before adding it again.",
        [ErrorCodes.TransactionAlreadyImported] = "That expense already came from a bank transaction."
    };

    /// <summary>
    /// <c>GROUP_REJOIN_NEEDS_NEW_INVITATION</c> for somebody another member removed. The
    /// refusal carries the reason, and "before you left" is the wrong thing to tell them.
    /// </summary>
    public const string RejoinAfterRemoval =
        "You were removed from this group, and this link was shared before that. " +
        "Ask someone in the group for a new one.";

    /// <summary>
    /// <c>GROUP_MEMBER_NOT_SETTLED</c> for one named person, from the balance the refusal
    /// carries: who, which way, and how much.
    /// </summary>
    /// <remarks>
    /// The table's own sentence has to cover leaving as well as removing, so it can only
    /// state the rule. Somebody pressing Remove on a row knows who they meant and wants to
    /// know what is standing in the way, which for a guest is a repayment the members have
    /// to record themselves -- so the amount and its direction are the useful part.
    /// <para>
    /// The sign is the group's: a positive balance is money owed to them.
    /// </para>
    /// </remarks>
    public static string NotSquare(string name, decimal balance) =>
        balance > 0
            ? $"{name} is still owed {balance.ToMoney()} in this group. Record what they were paid back, then remove them."
            : $"{name} still owes {Math.Abs(balance).ToMoney()} in this group. Record what they paid back, then remove them.";

    /// <summary>Whether the code has a message of its own; tests use it to keep the table complete.</summary>
    public static bool Knows(string code) => ByCode.ContainsKey(code);

    /// <summary>
    /// The message for a code, or for the status when the code is unknown or absent.
    /// </summary>
    public static string For(string? code, int? status = null) =>
        code is not null && ByCode.TryGetValue(code, out var message) ? message : ForStatus(status);

    public static string ForStatus(int? status) => status switch
    {
        400 => ByCode[ErrorCodes.BadRequest],
        401 => SessionExpired,
        403 => ByCode[ErrorCodes.Forbidden],
        404 => ByCode[ErrorCodes.NotFound],
        409 => ByCode[ErrorCodes.Conflict],
        >= 500 => ServerPrefix,
        _ => Generic
    };
}
