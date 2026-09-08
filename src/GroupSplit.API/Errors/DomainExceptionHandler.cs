using Microsoft.AspNetCore.Diagnostics;

namespace GroupSplit.API.Errors;

/// <summary>
/// Turns a <see cref="DomainException"/> into the problem response its status and code
/// describe. These are expected outcomes -- a member who has not settled, a rule that is
/// not editable -- so they are logged as information, with the code and without a stack
/// trace. Anything else is left for <see cref="UnhandledExceptionHandler"/>.
/// </summary>
/// <remarks>
/// With one exception, and it is named rather than described by its status.
/// <see cref="LeftBehindException"/> is a bug that happened to have something to tell the
/// caller -- somebody's bank granted access and this application could not store it -- and
/// why it happened is in an inner exception that Information-without-a-trace throws away.
/// It is the highest-consequence failure the application has and the one nobody could
/// diagnose from a message alone.
/// <para>
/// Named, and not "any 5xx", because <see cref="BadGatewayException"/> is a 5xx too and is
/// the opposite kind of thing: a provider having a bad moment, already logged as a warning
/// where it was raised, and the one outcome in the catalog that retrying fixes. Logging that
/// at Error with a stack trace turns an expected, retryable condition into something that
/// wakes somebody up.
/// </para>
/// </remarks>
internal sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
            return false;

        if (domainException is LeftBehindException)
        {
            logger.LogError(
                domainException,
                "{Method} {Path} failed with {ErrorCode} ({StatusCode}): {Reason}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                domainException.Code,
                domainException.Status,
                domainException.Message);
        }
        else
        {
            logger.LogInformation(
                "{Method} {Path} refused with {ErrorCode} ({StatusCode}): {Reason}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                domainException.Code,
                domainException.Status,
                domainException.Message);
        }

        await Problems.WriteAsync(
            httpContext,
            problemDetailsService,
            Problems.FromException(domainException),
            exception,
            cancellationToken);

        return true;
    }
}
