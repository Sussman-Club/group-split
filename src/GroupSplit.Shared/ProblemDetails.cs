using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
///     RFC 7807 problem details, used as the deserialization target for API error responses.
/// </summary>
public record ProblemDetails
{
    public string? Type { get; init; }

    public string? Title { get; init; }

    public int? Status { get; init; }

    public string? Detail { get; init; }

    public string? Instance { get; init; }

    [JsonExtensionData] public IDictionary<string, JsonElement>? Extensions { get; init; }
}
