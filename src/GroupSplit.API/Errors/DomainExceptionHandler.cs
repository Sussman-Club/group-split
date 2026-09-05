using Microsoft.AspNetCore.Diagnostics;

namespace GroupSplit.API.Errors;

/// <summary>
/// Turns a <see cref="DomainException"/> into the problem response its status and code
/// describe. These are expected outcomes -- a member who has not settled, a rule that is
/// not editable -- so they are logged as information, with the code and without a stack
/// trace. Anything else is left for <see cref="UnhandledExceptionHandler"/>.
/// </summary>
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

        logger.LogInformation(
            "{Method} {Path} refused with {ErrorCode} ({StatusCode}): {Reason}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            domainException.Code,
            domainException.Status,
            domainException.Message);

        await Problems.WriteAsync(
            httpContext,
            problemDetailsService,
            Problems.FromException(domainException),
            exception,
            cancellationToken);

        return true;
    }
}
