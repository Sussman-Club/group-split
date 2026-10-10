using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Errors;

/// <summary>
/// Answers the two ways the database itself settles a race -- a unique index refusing the
/// second of two inserts, an update or delete finding its row changed or gone -- with the
/// code the race is documented under, rather than the 500 an unhandled exception gets.
/// </summary>
/// <remarks>
/// The services check before they write, and say which name or which row in their own
/// words. Two requests can both pass that check, and the database is then the only thing
/// standing between them; this is what makes its answer the same answer the check gives.
/// <para>
/// Only the constraints named here. Any other unique violation is a write the code never
/// expected to make, which is a bug and stays a 500 with a trace id.
/// </para>
/// </remarks>
internal sealed class DatabaseConflictHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DatabaseConflictHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (DatabaseConflicts.Translate(exception) is not { } conflict)
            return false;

        logger.LogInformation(
            "{Method} {Path} lost a race with {ErrorCode} ({StatusCode}): {Reason}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            conflict.Code,
            conflict.Status,
            exception.InnerException?.Message ?? exception.Message);

        await Problems.WriteAsync(
            httpContext,
            problemDetailsService,
            Problems.FromException(conflict),
            exception,
            cancellationToken);

        return true;
    }
}

/// <summary>Which documented refusal a failed save stands for, if any.</summary>
public static class DatabaseConflicts
{
    /// <summary>
    /// The unique indexes a race can reach, and what a lost race on each means to the
    /// caller. Each is the index behind a check the service already makes.
    /// </summary>
    private static readonly Dictionary<string, (string Code, string Detail)> UniqueIndexes =
        new(StringComparer.Ordinal)
        {
            ["IX_Category_GroupId_Name"] = (ErrorCodes.CategoryNameTaken,
                "This group already has a category with that name."),
            ["IX_SplitRule_GroupId_Name"] = (ErrorCodes.SplitRuleNameTaken,
                "This group already has a split rule with that name."),
            ["IX_Transaction_BankTransactionId"] = (ErrorCodes.BankTransactionAlreadyFiled,
                "This imported transaction has already been added as an expense."),
            // One bill per expense: two first saves of one landing together.
            ["IX_Receipt_ExpenseId"] = (ErrorCodes.ConcurrentChange,
                "Somebody else saved a bill for this expense at the same moment. Look again before saving.")
        };

    /// <summary>
    /// The refusal <paramref name="exception"/> amounts to, or null when it is not a race
    /// this knows the answer to.
    /// </summary>
    public static DomainException? Translate(Exception exception) => exception switch
    {
        // First: it is a DbUpdateException too.
        DbUpdateConcurrencyException => new ConflictException(ErrorCodes.ConcurrentChange,
            "Somebody else changed this at the same moment. Nothing was saved; look again before retrying."),
        DbUpdateException
            {
                InnerException: PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: { } constraint
                }
            } when UniqueIndexes.TryGetValue(constraint, out var refusal)
            => new ConflictException(refusal.Code, refusal.Detail),
        _ => null
    };
}
