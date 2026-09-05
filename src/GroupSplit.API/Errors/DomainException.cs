using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Errors;

/// <summary>
/// A failure the API expects and has a contract for. Carries the status the response gets
/// and the code the client branches on; the message becomes the problem's <c>detail</c>, so
/// it has to be fit for a caller to read. Anything a service throws that is not one of
/// these is treated as a bug and answered with a 500 that says nothing about it.
/// </summary>
public abstract class DomainException(int status, string code, string message) : Exception(message)
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
