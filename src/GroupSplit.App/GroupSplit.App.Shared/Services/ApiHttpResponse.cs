using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

/// <summary>Preserves the API's problem contract for hand-written HTTP clients.</summary>
public sealed class ApiProblemException(ProblemDetails? problem, System.Net.HttpStatusCode status)
    : HttpRequestException(problem?.Detail ?? problem?.Title ?? "The request failed.", null, status)
{
    public ProblemDetails? Problem { get; } = problem;

    public string UserMessage => Errors.ErrorMessages.For(Problem?.GetExtensionString("code"), (int?)StatusCode);
}

internal static class ApiHttpResponse
{
    public static async Task EnsureApiSuccessAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ProblemDetails? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        throw new ApiProblemException(problem, response.StatusCode);
    }

    public static async Task<T?> GetFromApiJsonAsync<T>(this HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>();
    }
}
