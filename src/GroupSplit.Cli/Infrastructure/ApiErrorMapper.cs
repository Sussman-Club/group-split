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

    /// <summary>Flattens the per-field messages of a validation problem into one line each.</summary>
    private static IReadOnlyList<string>? ValidationMessages(ProblemDetails? problem, string? body)
    {
        if (problem is HttpValidationProblemDetails { Errors.Count: > 0 } typed)
        {
            return Flatten(typed.Errors);
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

    private static List<string> Flatten(IDictionary<string, string[]> errors)
        => errors
            .SelectMany(entry => entry.Value.Select(message => $"{entry.Key}: {message}"))
            .ToList();
}
