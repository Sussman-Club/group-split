namespace GroupSplit.Shared.Errors;

/// <summary>
/// The error codes the API puts in the <c>code</c> member of every problem response, and the
/// only thing a client should branch on: <c>title</c> and <c>detail</c> are free to change.
/// <para>
/// Declared once here so the API and the clients compile against the same list. They are
/// strings rather than an enum, as RFC 9457 practice has it: a code the API adds later then
/// reaches an old client as a string it does not recognise and falls back on, instead of as
/// a value it cannot deserialize. The catalog is documented in <c>docs/errors.md</c>; add a
/// row there when adding a code here.
/// </para>
/// </summary>
public static class ErrorCodes
{
    // ---- Generic, chosen from the status code when nothing more specific applies --------

    public const string BadRequest = "BAD_REQUEST";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string InternalError = "INTERNAL_ERROR";

    // ---- Not found (404) ----------------------------------------------------------------

    public const string GroupNotFound = "GROUP_NOT_FOUND";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string TransactionNotFound = "TRANSACTION_NOT_FOUND";
    public const string CategoryNotFound = "CATEGORY_NOT_FOUND";
    public const string SplitRuleNotFound = "SPLIT_RULE_NOT_FOUND";
    public const string GroupInvitationNotFound = "GROUP_INVITATION_NOT_FOUND";
    public const string GroupJoinLinkNotFound = "GROUP_JOIN_LINK_NOT_FOUND";
    public const string BankConnectionNotFound = "BANK_CONNECTION_NOT_FOUND";
    public const string BankTransactionNotFound = "BANK_TRANSACTION_NOT_FOUND";

    // ---- Forbidden (403) ----------------------------------------------------------------

    public const string GroupCannotRemoveSelf = "GROUP_CANNOT_REMOVE_SELF";
    public const string GroupInvitationNotYours = "GROUP_INVITATION_NOT_YOURS";

    // ---- Conflict (409): the request is well formed but the current state refuses it -----

    public const string GroupMemberNotSettled = "GROUP_MEMBER_NOT_SETTLED";
    public const string AccountNotSettled = "ACCOUNT_NOT_SETTLED";
    public const string TransactionPayerNotInGroup = "TRANSACTION_PAYER_NOT_IN_GROUP";
    public const string SplitUserNotInGroup = "SPLIT_USER_NOT_IN_GROUP";
    public const string SettlementWithSelf = "SETTLEMENT_WITH_SELF";
    public const string SettlementNothingToSettle = "SETTLEMENT_NOTHING_TO_SETTLE";
    public const string GroupInvitationAlreadySent = "GROUP_INVITATION_ALREADY_SENT";
    public const string GroupMemberAlreadyJoined = "GROUP_MEMBER_ALREADY_JOINED";
    public const string GroupCannotLeaveLastMember = "GROUP_CANNOT_LEAVE_LAST_MEMBER";
    public const string GroupJoinLinkExpired = "GROUP_JOIN_LINK_EXPIRED";
    public const string GroupJoinLinkRevoked = "GROUP_JOIN_LINK_REVOKED";
    public const string TransactionGroupLeft = "TRANSACTION_GROUP_LEFT";
    public const string CategoryNameTaken = "CATEGORY_NAME_TAKEN";
    public const string CategoryInUse = "CATEGORY_IN_USE";
    public const string SplitRuleNameTaken = "SPLIT_RULE_NAME_TAKEN";
    public const string SplitRuleInUse = "SPLIT_RULE_IN_USE";
    public const string BankSyncUnavailable = "BANK_SYNC_UNAVAILABLE";
    public const string BankTransactionAlreadyFiled = "BANK_TRANSACTION_ALREADY_FILED";
    public const string BankConnectionNeedsAttention = "BANK_CONNECTION_NEEDS_ATTENTION";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string PossibleDuplicateExpense = "POSSIBLE_DUPLICATE_EXPENSE";
    public const string TransactionAlreadyImported = "TRANSACTION_ALREADY_IMPORTED";

    // ---- Validation (400): the request itself is wrong ----------------------------------

    public const string SplitRuleInvalid = "SPLIT_RULE_INVALID";
    public const string SplitsInvalid = "SPLITS_INVALID";
    public const string SplitOnAPersonalExpense = "SPLIT_ON_A_PERSONAL_EXPENSE";
    public const string RuleUsersNotInGroup = "RULE_USERS_NOT_IN_GROUP";

    // ---- Unprocessable (422): the request is understood and coherent, and still cannot --
    // ---- be carried out, because acting on it would break an invariant ------------------

    public const string SplitsDoNotSumToAmount = "SPLITS_DO_NOT_SUM_TO_AMOUNT";
    public const string BankTransactionIsCredit = "BANK_TRANSACTION_IS_CREDIT";

    // ---- Bad gateway (502): somebody else's service is in the path and did not answer ---

    public const string BankProviderUnavailable = "BANK_PROVIDER_UNAVAILABLE";

    /// <summary>
    /// The access this connection was holding can no longer be read, so nothing can be done
    /// with it -- not synced, not repaired in update mode, not removed at the provider. All
    /// three need the token. Linking the bank again is the only way forward.
    /// </summary>
    public const string BankConnectionUnrecoverable = "BANK_CONNECTION_UNRECOVERABLE";

    // ---- Server error (500) that still has something to say -------------------------
    //
    // Ordinarily a 500 tells a caller a trace id and nothing else, because a bug has no
    // useful description. These are different: the request failed here, but the bank had
    // already granted access by the time it did, and what became of that access is
    // something the person is entitled to know rather than guess at.

    public const string BankLinkNotSaved = "BANK_LINK_NOT_SAVED";

    public const string BankLinkNotSavedAccessRemains = "BANK_LINK_NOT_SAVED_ACCESS_REMAINS";

    /// <summary>
    /// Storing the connection failed, but the item behind it was written down first, so
    /// finishing it later needs nothing from the person and costs no second item.
    /// </summary>
    public const string BankLinkWillBeFinished = "BANK_LINK_WILL_BE_FINISHED";

    /// <summary>
    /// The generic code for a status, used when a response was produced by something that
    /// knows nothing about the domain: routing, authentication, model binding.
    /// </summary>
    public static string ForStatus(int status) => status switch
    {
        400 => BadRequest,
        401 => Unauthenticated,
        403 => Forbidden,
        404 => NotFound,
        409 => Conflict,
        422 => ValidationFailed,
        >= 500 => InternalError,
        _ => $"HTTP_{status}"
    };
}
