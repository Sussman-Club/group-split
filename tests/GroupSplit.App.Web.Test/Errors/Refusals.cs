using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Errors;

/// <summary>
/// A domain refusal shaped the way the generated client hands one over, for a test that
/// wants to see what the app does with one rather than to pick the problem apart.
/// </summary>
/// <remarks>
/// <see cref="ApiErrorsTest"/> builds its own, because it is about the reading itself and
/// needs problems with pieces missing. Everything else only needs a status and a code.
/// </remarks>
internal static class Refusals
{
    /// <summary>What the API writes with.</summary>
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    public static ApiException<ProblemDetails> Of(int status, string code)
    {
        var members = new Dictionary<string, object?>
        {
            ["type"] = "https://groupsplit.app/errors/x",
            ["title"] = "A title for developers",
            ["status"] = status,
            ["detail"] = "A detail for developers",
            ["code"] = code,
            ["traceId"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            JsonSerializer.Serialize(members, Api), GroupSplitSerializer.Options)!;

        return new ApiException<ProblemDetails>("refused", status, JsonSerializer.Serialize(problem, Api),
            new Dictionary<string, IEnumerable<string>>(), problem, null!);
    }
}
