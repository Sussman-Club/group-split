using System.Net;
using System.Text.Json;
using GroupSplit.Cli.Api;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Turns a failed API call into the CLI's error contract: the envelope on stderr and the
/// exit code. The API's own <c>code</c> is passed straight through, so the slug an agent
/// sees here is the one documented in <c>docs/errors.md</c>.
/// </summary>
public static class ApiErrorMapper
{
    public static CliException Map(ApiException exception)
    {
        var problem = ReadProblem(exception);
        var status = (HttpStatusCode)exception.StatusCode;

        var code = problem?.Code ?? FallbackCode(status);
        var message = Describe(problem, status);
        var details = ValidationMessages(problem, exception.Response);

        var error = new CliError(message, code, Remediation(status, code), details)
        {
            TraceId = problem?.TraceId
        };

        return new CliException(error, ExitCodeFor(status), exception);
    }

    /// <summary>
    /// Status to exit code. 401 and 403 both mean "your identity is the problem", which is
    /// the distinction exit code 2 exists to make; everything a caller could fix by
    /// changing arguments is 3; the rest is a plain failure.
    /// </summary>
    private static int ExitCodeFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ExitCodes.AuthRequired,
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity => ExitCodes.InvalidInput,
        _ => ExitCodes.Error
    };

    private static string Describe(ProblemDetails? problem, HttpStatusCode status)
    {
        var text = problem?.Detail ?? problem?.Title;

        return string.IsNullOrWhiteSpace(text)
            ? $"The server returned {(int)status} {status}."
            : text;
    }

    private static string FallbackCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => Shared.Errors.ErrorCodes.Unauthenticated,
        HttpStatusCode.Forbidden => Shared.Errors.ErrorCodes.Forbidden,
        HttpStatusCode.NotFound => Shared.Errors.ErrorCodes.NotFound,
        HttpStatusCode.Conflict => Shared.Errors.ErrorCodes.Conflict,
        HttpStatusCode.BadRequest => Shared.Errors.ErrorCodes.BadRequest,
        _ => ErrorCodes.ServerError
    };

    private static string? Remediation(HttpStatusCode status, string code) => status switch
    {
        HttpStatusCode.Unauthorized => "Run: groupsplit auth login",
        HttpStatusCode.Forbidden => "The account you are signed in as is not allowed to do this. "
                                    + "Check with: groupsplit auth status",
        HttpStatusCode.NotFound => "Check the id. List what you can see with: groupsplit groups list",
        HttpStatusCode.BadRequest when code == Shared.Errors.ErrorCodes.ValidationFailed
            => "Fix the values listed above and try again.",
        // The one refusal with two answers rather than a fix. Both are named, because
        // neither is the obvious one: which applies is a question about the money, and only
        // the person who spent it can answer it.
        HttpStatusCode.Conflict when code == Shared.Errors.ErrorCodes.PossibleDuplicateExpense
            => "It is the same payment: groupsplit inbox link <row-id> <transaction-id>   "
               + "You really paid twice: groupsplit inbox file <row-id> --file-anyway   "
               + "Not a match at all: groupsplit inbox dismiss-match <row-id> <transaction-id>",
        _ => null
    };

    /// <summary>
    /// For a status the OpenAPI document declares, the generated client deserializes the
    /// body and hands it over as <c>ApiException&lt;ProblemDetails&gt;.Result</c>, leaving
    /// <c>Response</c> null -- it streams rather than buffering the text. Only an
    /// undeclared status arrives as a raw string, so both have to be read.
    /// </summary>
    private static ProblemDetails? ReadProblem(ApiException exception) => exception switch
    {
        ApiException<HttpValidationProblemDetails> validation => validation.Result,
        ApiException<ProblemDetails> typed => typed.Result,
        _ => TryReadProblem(exception.Response)
    };

    private static ProblemDetails? TryReadProblem(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProblemDetails>(body, GroupSplitSerializer.Options);
        }
        catch (JsonException)
        {
            // Not every failure comes from the API itself -- a proxy in front of it can
            // answer with HTML. Falling back to the status code still gives a usable error.
            return null;
        }
    }

    /// <summary>
    /// The per-field messages of a validation problem, or -- for the two refusals that name
    /// rows instead of fields -- the groups standing in the way of deleting an account, and
    /// the expenses an imported row may already be.
    /// </summary>
    /// <remarks>
    /// All three land in <c>details</c> because that is where a caller already looks for
    /// "what exactly was wrong", and in both extension cases the list is right there on the
    /// problem. Without this a refusal reads "settle up in every group" or "this looks like
    /// an expense you have already recorded" with no way to learn which -- and for the
    /// second, both answers to it need the id of the expense it matched.
    /// </remarks>
    private static IReadOnlyList<string>? ValidationMessages(ProblemDetails? problem, string? body)
    {
        if (problem is HttpValidationProblemDetails { Errors.Count: > 0 } typed)
        {
            return Flatten(typed.Errors);
        }

        if (problem?.Code == Shared.Errors.ErrorCodes.AccountNotSettled
            && problem.GetExtension<List<OutstandingBalance>>(
                ProblemDetails.OutstandingBalancesExtension, GroupSplitSerializer.Options)
                is { Count: > 0 } outstanding)
        {
            return outstanding
                .Select(balance => balance.Balance < 0
                    ? $"{balance.GroupName}: you owe {-balance.Balance:N2}"
                    : $"{balance.GroupName}: you are owed {balance.Balance:N2}")
                .ToList();
        }

        if (problem?.Code == Shared.Errors.ErrorCodes.PossibleDuplicateExpense
            && problem.GetExtension<List<ExpenseMatchResponse>>(
                ProblemDetails.MatchesExtension, GroupSplitSerializer.Options)
                is { Count: > 0 } matches)
        {
            // The id first, because it is the argument both answers take, and the rest of
            // the line is what a person needs to recognise the expense by.
            return matches.Select(Describe).ToList();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<HttpValidationProblemDetails>(
                body, GroupSplitSerializer.Options);

            return parsed?.Errors is { Count: > 0 } errors ? Flatten(errors) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One suggested expense on one line: the id to act on, then enough to tell whether it
    /// really is the same payment -- what it was called, for how much, where, and how far
    /// off the row it is on each of the two axes that decided the suggestion.
    /// </summary>
    internal static string Describe(ExpenseMatchResponse match)
    {
        var apart = match.DaysApart == 1 ? "1 day apart" : $"{match.DaysApart} days apart";

        // "apart", not "more": the difference is never signed, so which way round it is is
        // not something this knows -- and a tip added after the receipt is only the usual
        // reason, not the only one.
        return $"{match.TransactionId}  {match.Name} {match.Amount:N2} {match.Currency} "
               + $"in {match.Where}, {apart}"
               + (match.AmountDifference == 0
                   ? ", same amount"
                   : $", {match.AmountDifference:N2} apart");
    }

    private static List<string> Flatten(IDictionary<string, string[]> errors)
        => errors
            .SelectMany(entry => entry.Value.Select(message => $"{entry.Key}: {message}"))
            .ToList();
}
