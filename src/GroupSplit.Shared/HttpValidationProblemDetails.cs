namespace GroupSplit.Shared;

/// <summary>
///     The problem details a validation failure produces: the standard members plus
///     <see cref="Errors"/>, keyed by the member that failed. The name matches the schema the
///     API's OpenAPI document declares for <c>ProducesValidationProblem</c>, which is how the
///     generated client finds it.
/// </summary>
public record HttpValidationProblemDetails : ProblemDetails
{
    public IDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}
