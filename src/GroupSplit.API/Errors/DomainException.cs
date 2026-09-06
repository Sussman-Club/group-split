using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Errors;

/// <summary>
/// A failure the API expects and has a contract for. Carries the status the response gets
/// and the code the client branches on; the message becomes the problem's <c>detail</c>, so
/// it has to be fit for a caller to read. Anything a service throws that is not one of
/// these is treated as a bug and answered with a 500 that says nothing about it.
/// </summary>
public abstract class DomainException(int status, string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    /// <summary>
    /// Members that travel with the problem details, for the cases where a code alone
    /// leaves the caller guessing: the balance that blocks a removal, the groups that block
    /// a deletion. Keys are written to the wire as given, so use camelCase.
    /// </summary>
    public Dictionary<string, object?> Extensions { get; } = new();

    public DomainException WithExtension(string name, object? value)
    {
        Extensions[name] = value;
        return this;
    }
}

/// <summary>The resource the request names, or one it refers to, does not exist for this caller.</summary>
public sealed class NotFoundException(string code, string message)
    : DomainException(StatusCodes.Status404NotFound, code, message);

/// <summary>The request is well formed, but the current state refuses it.</summary>
public sealed class ConflictException(string code, string message)
    : DomainException(StatusCodes.Status409Conflict, code, message);

/// <summary>The caller is known and is not allowed to do this.</summary>
public sealed class ForbiddenException(string code, string message)
    : DomainException(StatusCodes.Status403Forbidden, code, message);

/// <summary>The request itself is wrong in a way the annotations on the model cannot express.</summary>
public sealed class ValidationException(string code, string message)
    : DomainException(StatusCodes.Status400BadRequest, code, message);

/// <summary>
/// The request is well formed and internally coherent, and carrying it out anyway would
/// leave the data saying something untrue.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ValidationException"/>, which says a field is wrong, and from
/// <see cref="ConflictException"/>, which says the stored state refuses it. This one says
/// the request contradicts itself against what it is being applied to: splits that do not
/// sum to the amount they divide are the case it exists for, and the difference matters to
/// a client, which can show the shortfall rather than a field error.
/// </remarks>
public sealed class UnprocessableException(string code, string message)
    : DomainException(StatusCodes.Status422UnprocessableEntity, code, message);

/// <summary>
/// A service this one depends on refused or could not be reached, and the request cannot be
/// answered without it.
/// </summary>
/// <remarks>
/// The first code in the catalog that is nobody's fault here and nobody's fault there
/// either: a bank aggregator having a bad minute. A 5xx rather than a 4xx because retrying
/// the identical request later is the right thing for a caller to do, which is precisely
/// what a 4xx tells them not to bother with.
/// </remarks>
public sealed class BadGatewayException(string code, string message, Exception? inner = null)
    : DomainException(StatusCodes.Status502BadGateway, code, message, inner);
