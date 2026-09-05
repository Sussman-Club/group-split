using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
///     RFC 9457 problem details, the shape of every non-2xx API response and the type the
///     generated client deserializes them into. The API adds the members below
///     <see cref="Instance"/> as extensions; <see cref="Extensions"/> catches them, and the
///     accessors in <see cref="ProblemDetailsExtensions"/> read the ones with a contract.
/// </summary>
/// <remarks>
///     Deliberately nothing else on the record. A member named after a wire property --
///     <c>Code</c>, say, even marked <c>[JsonIgnore]</c> -- would claim the JSON property of
///     the same name and System.Text.Json would skip it rather than hand it to the extension
///     dictionary, which is how <c>code</c> went missing once.
/// </remarks>
public record ProblemDetails
{
    /// <summary>Extension member: the stable error code, from <see cref="Errors.ErrorCodes"/>.</summary>
    public const string CodeExtension = "code";

    /// <summary>Extension member: the W3C trace id of the request, also on the server's log line.</summary>
    public const string TraceIdExtension = "traceId";

    /// <summary>Extension member on <see cref="HttpValidationProblemDetails"/>: the per-field messages.</summary>
    public const string ErrorsExtension = "errors";

    /// <summary>
    /// Extension member on an <see cref="Errors.ErrorCodes.AccountNotSettled"/> problem: the
    /// <see cref="OutstandingBalance"/> list naming the groups that block the deletion.
    /// </summary>
    public const string OutstandingBalancesExtension = "outstandingBalances";

    public string? Type { get; init; }

    public string? Title { get; init; }

    public int? Status { get; init; }

    public string? Detail { get; init; }

    public string? Instance { get; init; }

    [JsonExtensionData] public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public static class ProblemDetailsExtensions
{
    extension(ProblemDetails problem)
    {
        /// <summary>
        /// The error code, or null when the response carried none. Compare against
        /// <see cref="Errors.ErrorCodes"/>, and keep a default arm: a newer API may send a
        /// code this build has never heard of.
        /// </summary>
        public string? Code => problem.GetExtensionString(ProblemDetails.CodeExtension);

        /// <summary>The trace id, or null when the response did not carry one.</summary>
        public string? TraceId => problem.GetExtensionString(ProblemDetails.TraceIdExtension);

        public string? GetExtensionString(string name) =>
            problem.Extensions is not null
            && problem.Extensions.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        /// <summary>
        /// Reads a structured extension member, or default when it is absent. The options
        /// should be the ones the client reads every other payload with, since the member
        /// was written by the same serializer as the rest of the response.
        /// </summary>
        public T? GetExtension<T>(string name, JsonSerializerOptions? options = null) =>
            problem.Extensions is not null
            && problem.Extensions.TryGetValue(name, out var element)
            && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? element.Deserialize<T>(options)
                : default;
    }
}
