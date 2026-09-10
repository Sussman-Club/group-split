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

        [ErrorCodes.GroupCannotRemoveSelf] = "You cannot remove yourself from a group. Leave it instead.",
        [ErrorCodes.GroupInvitationNotFound] = "That invitation is no longer open.",
        [ErrorCodes.GroupJoinLinkNotFound] = "That join link is not one of ours. Check you copied the whole of it.",
        [ErrorCodes.GroupJoinLinkExpired] = "That join link has expired. Ask someone in the group for a new one.",
        [ErrorCodes.GroupJoinLinkRevoked] = "That join link has been withdrawn. Ask someone in the group for a new one.",
        [ErrorCodes.GroupCannotLeaveLastMember] = "You are the only member left. Archive the group instead of leaving it.",
        [ErrorCodes.GroupInvitationNoName] = "An invitation needs a name to make it out to.",
        [ErrorCodes.GroupMemberNotJoined] =
            "That person has been invited and has not joined, so there is no membership to remove. " +
            "Withdraw the invitation instead.",
        [ErrorCodes.TransactionGroupLeft] = "You are no longer in this transaction's group, so it cannot be changed.",

        [ErrorCodes.GroupMemberNotSettled] = "This member still has a balance in the group. Settle up before removing them.",
        [ErrorCodes.AccountNotSettled] = "Settle up in every group before deleting your account.",
        [ErrorCodes.TransactionPayerNotInGroup] =
            "The person who paid is neither a member of this group nor invited to it.",
        [ErrorCodes.SplitUserNotInGroup] =
            "One of the shares names someone who is neither in this group nor invited to it.",
        [ErrorCodes.SettlementWithSelf] = "You cannot settle up with yourself.",
        // Their balance is real and on the page. What is missing is an account on the other
        // end of the payment, which is why this is not "not a member of this group".
        [ErrorCodes.SettlementWithPendingInvitee] =
            "That person has been invited and has not joined yet, so there is nobody to pay. " +
            "Their balance stands until they accept.",
        [ErrorCodes.SettlementNothingToSettle] = "You are already square with everybody in this group.",
        [ErrorCodes.CategoryNameTaken] = "This group already has a category with that name.",
        [ErrorCodes.CategoryInUse] = "This category still has expenses filed under it. Move them first.",
        [ErrorCodes.SplitRuleNameTaken] = "This group already has a rule with that name.",
        [ErrorCodes.SplitRuleInUse] = "This rule is still the default for a category. Point the category elsewhere first.",
        // "Already" and not "this group already": a merchant is shared, so the one it
        // collides with may well be a place somebody else's bank reported.
        [ErrorCodes.MerchantNameTaken] = "There is already a merchant with that name.",
        [ErrorCodes.MerchantInUse] = "This merchant still has transactions pointing at it. Move them first.",

        [ErrorCodes.SplitRuleInvalid] = "The split does not add up. Check it and try again.",
        [ErrorCodes.SplitsInvalid] = "The shares are not valid. Each person can appear only once.",
        [ErrorCodes.SplitOnAPersonalExpense] = "A personal expense is not shared with anybody, so it cannot be split.",
        [ErrorCodes.SplitsDoNotSumToAmount] = "The shares have to add up to the amount of the expense.",
        [ErrorCodes.RuleUsersNotInGroup] = "The rule names someone who is not in the group.",

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
