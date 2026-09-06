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
        [ErrorCodes.SplitRuleNotFound] = "That split rule no longer exists.",

        [ErrorCodes.GroupCannotRemoveSelf] = "You cannot remove yourself from a group.",

        [ErrorCodes.GroupMemberNotSettled] = "This member still has a balance in the group. Settle up before removing them.",
        [ErrorCodes.AccountNotSettled] = "Settle up in every group before deleting your account.",
        [ErrorCodes.TransactionPayerNotInGroup] = "The person who paid is not a member of this group.",
        [ErrorCodes.SettlementWithSelf] = "You cannot settle up with yourself.",
        [ErrorCodes.CategoryNameTaken] = "This group already has a category with that name.",
        [ErrorCodes.CategoryInUse] = "This category still has expenses filed under it. Move them first.",
        [ErrorCodes.SplitRuleNameTaken] = "This group already has a rule with that name.",
        [ErrorCodes.SplitRuleInUse] = "This rule is still the default for a category. Point the category elsewhere first.",

        [ErrorCodes.SplitRuleInvalid] = "The split does not add up. Check it and try again.",
        [ErrorCodes.RuleUsersNotInGroup] = "The rule names someone who is not in the group."
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
