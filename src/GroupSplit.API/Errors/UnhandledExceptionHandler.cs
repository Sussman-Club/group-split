using System.Diagnostics;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics;

namespace GroupSplit.API.Errors;

/// <summary>
/// The last handler: whatever reaches it is a bug, or infrastructure that failed. The
/// response is a 500 that carries nothing of the exception -- not the message, not the
/// type, no SQL -- only the trace id; the same trace id is on the error log line written
/// here, so a screenshot of the response is enough to find the cause.
/// <para>
/// Since .NET 10 the exception handler middleware writes no diagnostics of its own once a
/// handler has taken the exception, which is why this one logs and marks the activity
/// itself rather than relying on the middleware to.
/// </para>
/// </summary>
public sealed class UnhandledExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public const string Detail =
        "Something went wrong on our side. If it keeps happening, quote the trace id when reporting it.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Problems.TraceIdOf(httpContext);

        logger.LogError(
            exception,
            "Unhandled exception while handling {Method} {Path}; the response carries trace id {TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        if (Activity.Current is { } activity)
        {
            activity.AddException(exception);
            activity.SetStatus(ActivityStatusCode.Error);
        }

        await Problems.WriteAsync(
            httpContext,
            problemDetailsService,
            Problems.Create(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, Detail),
            exception,
            cancellationToken);

        return true;
    }
}
