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
    public const string RuleNotFound = "RULE_NOT_FOUND";
    public const string RuleVersionNotFound = "RULE_VERSION_NOT_FOUND";

    // ---- Forbidden (403) ----------------------------------------------------------------

    public const string GroupCannotRemoveSelf = "GROUP_CANNOT_REMOVE_SELF";

    // ---- Conflict (409): the request is well formed but the current state refuses it -----

    public const string GroupMemberNotSettled = "GROUP_MEMBER_NOT_SETTLED";
    public const string AccountNotSettled = "ACCOUNT_NOT_SETTLED";
    public const string GroupHasNoRule = "GROUP_HAS_NO_RULE";
    public const string RuleCategoryTaken = "RULE_CATEGORY_TAKEN";
    public const string RuleNotEditable = "RULE_NOT_EDITABLE";
    public const string RuleNotDeletable = "RULE_NOT_DELETABLE";
    public const string RuleNoUserTransactions = "RULE_NO_USER_TRANSACTIONS";
    public const string RuleVersionHasRemovedMember = "RULE_VERSION_HAS_REMOVED_MEMBER";
    public const string TransactionPayerNotInGroup = "TRANSACTION_PAYER_NOT_IN_GROUP";

    // ---- Validation (400): the request itself is wrong ----------------------------------

    public const string TransactionRuleRequired = "TRANSACTION_RULE_REQUIRED";
    public const string TransactionPayerRequiresRule = "TRANSACTION_PAYER_REQUIRES_RULE";
    public const string RulePercentagesInvalid = "RULE_PERCENTAGES_INVALID";
    public const string RuleUsersNotInGroup = "RULE_USERS_NOT_IN_GROUP";
    public const string RuleSharesEmpty = "RULE_SHARES_EMPTY";

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
        >= 500 => InternalError,
        _ => $"HTTP_{status}"
    };
}
