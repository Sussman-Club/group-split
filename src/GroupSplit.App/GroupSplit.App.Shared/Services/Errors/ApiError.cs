using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Errors;

/// <summary>
/// The kinds of failure a call to the API can end in, from the point of view of what the
/// person in front of the screen can do about it.
/// </summary>
public enum ApiErrorKind
{
    /// <summary>The API refused the request for a reason it named: a 4xx problem with a code.</summary>
    Refused,

    /// <summary>A 400 carrying per-field messages.</summary>
    Validation,

    /// <summary>A 401. The session behind the cookie is gone; signing in again is the only fix.</summary>
    Unauthenticated,

    /// <summary>The request got no answer: no connection, a timeout, a server that is not there.</summary>
    Network,

    /// <summary>A 5xx. A bug or an outage on our side, identified by its trace id.</summary>
    Server,

    /// <summary>A status the contract does not cover and no problem body to explain it.</summary>
    Unknown
}

/// <summary>
/// One failed API call, read into what the UI needs: a kind to decide what to do, a message
/// a person can read, and the code and problem details for the few places that branch on
/// them. Produced by <see cref="ApiErrors.Read"/>.
/// </summary>
public sealed record ApiError(
    ApiErrorKind Kind,
    string Message,
    string? Code = null,
    int? Status = null,
    string? TraceId = null,
    ProblemDetails? Problem = null,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    /// <summary>
    /// A structured extension member of the problem, read with the same serializer settings
    /// the generated client reads everything else with.
    /// </summary>
    public T? Extension<T>(string name) =>
        Problem is null ? default : Problem.GetExtension<T>(name, GroupSplitSerializer.Options);
}
