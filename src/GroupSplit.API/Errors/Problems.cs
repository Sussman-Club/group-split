using System.Diagnostics;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;
using SharedProblemDetails = GroupSplit.Shared.ProblemDetails;

namespace GroupSplit.API.Errors;

/// <summary>
/// The one place that knows what a problem response looks like. Endpoints that answer with
/// a problem directly, the exception handlers, and the customization the framework applies
/// to the problems it generates itself all go through here, so the shape documented in
/// <c>docs/errors.md</c> is produced by exactly one piece of code.
/// </summary>
public static class Problems
{
    /// <summary>
    /// The prefix of every <c>type</c>. An opaque identifier for now, not a page that
    /// resolves; stable, so a client may key on it, though <c>code</c> is the intended key.
    /// </summary>
    public const string TypeBaseUri = "https://groupsplit.app/errors/";

    public const string ContentType = "application/problem+json";

    /// <summary>Short, human titles by code. Anything absent falls back to the status phrase.</summary>
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        [ErrorCodes.ValidationFailed] = "One or more validation errors occurred.",
        [ErrorCodes.Unauthenticated] = "Sign in to continue",
        [ErrorCodes.InternalError] = "Something went wrong",

        [ErrorCodes.GroupNotFound] = "Group not found",
        [ErrorCodes.UserNotFound] = "User not found",
        [ErrorCodes.AccountNotFound] = "Account not found",
        [ErrorCodes.TransactionNotFound] = "Transaction not found",
        [ErrorCodes.RuleNotFound] = "Rule not found",
        [ErrorCodes.RuleVersionNotFound] = "Rule version not found",

        [ErrorCodes.GroupCannotRemoveSelf] = "You cannot remove yourself from a group",

        [ErrorCodes.GroupMemberNotSettled] = "Member has an outstanding balance",
        [ErrorCodes.AccountNotSettled] = "Account has outstanding balances",
        [ErrorCodes.GroupHasNoRule] = "Group has no rule to record against",
        [ErrorCodes.RuleCategoryTaken] = "Category already in use",
        [ErrorCodes.RuleNotEditable] = "Rule cannot be edited",
        [ErrorCodes.RuleNotDeletable] = "Rule cannot be deleted",
        [ErrorCodes.RuleNoUserTransactions] = "Rule does not accept transactions",
        [ErrorCodes.RuleVersionHasRemovedMember] = "Rule includes a former member",
        [ErrorCodes.TransactionPayerNotInGroup] = "Payer is not a member of the group",

        [ErrorCodes.TransactionRuleRequired] = "A rule is required",
        [ErrorCodes.TransactionPayerRequiresRule] = "A rule is required to pay for someone else",
        [ErrorCodes.RulePercentagesInvalid] = "Percentages must add up to 100",
        [ErrorCodes.RuleUsersNotInGroup] = "Rule names someone outside the group",
        [ErrorCodes.RuleSharesEmpty] = "Nobody holds a share"
    };

    /// <summary>The <c>type</c> URI for a code: <c>GROUP_NOT_FOUND</c> becomes <c>.../group-not-found</c>.</summary>
    public static string TypeFor(string code) => TypeBaseUri + code.ToLowerInvariant().Replace('_', '-');

    public static string TitleFor(string code, int status) =>
        Titles.TryGetValue(code, out var title) ? title : ReasonPhrases.GetReasonPhrase(status);

    /// <summary>The trace id a response carries, which is also what the log line for it carries.</summary>
    public static string TraceIdOf(HttpContext httpContext) =>
        Activity.Current?.Id ?? httpContext.TraceIdentifier;

    public static ProblemDetails Create(
        int status,
        string code,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = TypeFor(code),
            Title = TitleFor(code, status),
            Detail = detail
        };

        problem.Extensions[SharedProblemDetails.CodeExtension] = code;

        if (extensions is not null)
        {
            foreach (var (name, value) in extensions)
            {
                problem.Extensions[name] = value;
            }
        }

        return problem;
    }

    public static ProblemDetails FromException(DomainException exception) =>
        Create(exception.Status, exception.Code, exception.Message, exception.Extensions);

    /// <summary>
    /// Applied to every problem before it is written, whoever produced it: the exception
    /// handlers, an endpoint returning <see cref="Results.Problem(ProblemDetails)"/>, the
    /// validation filter, status code pages for a bare 401 or 404. Fills in what the
    /// producer did not know: a code where there is none, the trace id, the instance, and a
    /// <c>type</c> in our namespace rather than a link into the HTTP RFC.
    /// </summary>
    public static void Enrich(HttpContext httpContext, ProblemDetails problem)
    {
        var status = problem.Status ??= httpContext.Response.StatusCode;

        if (!problem.Extensions.TryGetValue(SharedProblemDetails.CodeExtension, out var existing)
            || existing is not string code
            || string.IsNullOrEmpty(code))
        {
            code = problem is HttpValidationProblemDetails
                ? ErrorCodes.ValidationFailed
                : ErrorCodes.ForStatus(status);

            problem.Extensions[SharedProblemDetails.CodeExtension] = code;
        }

        problem.Type = TypeFor(code);
        problem.Title ??= TitleFor(code, status);
        problem.Instance ??= httpContext.Request.Path;
        problem.Extensions[SharedProblemDetails.TraceIdExtension] = TraceIdOf(httpContext);
    }

    /// <summary>
    /// Writes a problem as the response, through the problem details service so
    /// <see cref="Enrich"/> runs, and by hand when the service declines: it declines when
    /// the client asked for something other than JSON, and the contract is problem+json
    /// whatever the client asked for.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext httpContext,
        IProblemDetailsService problemDetailsService,
        ProblemDetails problem,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
            // The exception handler middleware clears the endpoint before it hands over,
            // so the one the request was routed to is only on the feature.
            AdditionalMetadata = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata
        });

        if (written) return;

        Enrich(httpContext, problem);
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, ContentType, cancellationToken);
    }

    // ---- For endpoints that answer with a problem without throwing ----------------------

    public static IResult NotFound(string code, string detail) =>
        Results.Problem(Create(StatusCodes.Status404NotFound, code, detail));

    public static IResult Conflict(string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        Results.Problem(Create(StatusCodes.Status409Conflict, code, detail, extensions));
}
