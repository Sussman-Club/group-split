using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.App.Shared.Services.Errors;

/// <summary>
/// Reads a failed API call into an <see cref="ApiError"/>. The one place that knows how the
/// generated client reports failures -- an <see cref="ApiException"/> per status, typed with
/// the problem details when the OpenAPI document declared them and carrying the raw body
/// when it did not -- and how the transport reports never getting an answer.
/// </summary>
public static class ApiErrors
{
    /// <summary>
    /// Whether the exception is one an API call can end in, as opposed to a bug in the
    /// caller. Only these are turned into messages; anything else is left to propagate to
    /// the error boundary, which is where a bug belongs.
    /// </summary>
    public static bool IsApiFailure(Exception exception) =>
        exception is ApiException or HttpRequestException or TaskCanceledException or TimeoutException;

    /// <summary>
    /// A cancellation that is not a timeout: the page that made the call went away. There is
    /// nothing to show anyone, and nothing went wrong.
    /// </summary>
    public static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException { InnerException: not TimeoutException };

    public static ApiError Read(Exception exception) => exception switch
    {
        ApiException api => Read(api),
        HttpRequestException => new ApiError(ApiErrorKind.Network, ErrorMessages.Network),
        TimeoutException or TaskCanceledException { InnerException: TimeoutException } =>
            new ApiError(ApiErrorKind.Network, ErrorMessages.Network),
        _ => new ApiError(ApiErrorKind.Unknown, ErrorMessages.Generic)
    };

    private static ApiError Read(ApiException exception)
    {
        var problem = exception switch
        {
            ApiException<HttpValidationProblemDetails> validation => validation.Result,
            ApiException<ProblemDetails> typed => typed.Result,
            // A status the endpoint did not declare arrives untyped, body and all. The API
            // still answered with problem details, so read them.
            _ => Parse(exception.Response)
        };

        var status = exception.StatusCode;
        var code = problem?.Code;
        var traceId = problem?.TraceId;

        if (status == 401)
            return new ApiError(ApiErrorKind.Unauthenticated, ErrorMessages.SessionExpired,
                code ?? ErrorCodes.Unauthenticated, status, traceId, problem);

        if (status >= 500)
            return new ApiError(ApiErrorKind.Server, ErrorMessages.Server(traceId),
                code ?? ErrorCodes.InternalError, status, traceId, problem);

        if (problem is HttpValidationProblemDetails { Errors.Count: > 0 } invalid)
        {
            var fieldErrors = invalid.Errors.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            // The first field message is the most useful sentence available: the API wrote
            // it about the exact value that was wrong.
            var first = fieldErrors.Values.SelectMany(messages => messages).FirstOrDefault();

            return new ApiError(ApiErrorKind.Validation, first ?? ErrorMessages.For(code, status),
                code ?? ErrorCodes.ValidationFailed, status, traceId, problem, fieldErrors);
        }

        if (code is not null)
            return new ApiError(ApiErrorKind.Refused, ErrorMessages.For(code, status),
                code, status, traceId, problem);

        return new ApiError(ApiErrorKind.Unknown, ErrorMessages.ForStatus(status),
            null, status, traceId, problem);
    }

    /// <summary>Problem details out of a raw body, or null when the body is not that.</summary>
    private static ProblemDetails? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            return JsonSerializer.Deserialize<HttpValidationProblemDetails>(body, GroupSplitSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
