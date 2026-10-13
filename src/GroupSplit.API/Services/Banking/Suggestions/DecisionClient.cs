using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking.Suggestions;

/// <summary>
/// Asks a Microsoft-Decision-1 deployment one multiple-choice question.
/// </summary>
public interface IDecisionClient
{
    /// <summary>
    /// The option the model picks for <paramref name="state"/>, with its confidence.
    /// </summary>
    /// <param name="options">
    /// Option label to a description of it. Between 2 and 255 of them; the service refuses
    /// anything else.
    /// </param>
    Task<DecisionAnswer?> ChooseAsync(
        object state,
        string instructions,
        IReadOnlyDictionary<string, string> options,
        CancellationToken ct = default);
}

public sealed record DecisionAnswer(string Choice, double Confidence);

/// <summary>
/// The REST call. Not the OpenAI SDK the receipt reader uses: Decision-1 is not a chat
/// model, and it is served on the Microsoft provider's own route of the same resource.
/// </summary>
/// <remarks>
/// <c>POST {resource}/providers/microsoft/v1/systemone</c> with the deployment as the
/// model, a <c>state</c> to judge, and named questions. A <c>choice</c> question answers
/// with the picked option and a calibrated confidence.
/// </remarks>
public sealed class DecisionClient(HttpClient http, IOptions<FilingSuggestionOptions> options) : IDecisionClient
{
    private const string Question = "filing";

    public async Task<DecisionAnswer?> ChooseAsync(
        object state,
        string instructions,
        IReadOnlyDictionary<string, string> choices,
        CancellationToken ct = default)
    {
        var settings = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.Endpoint!, "providers/microsoft/v1/systemone"));

        request.Headers.Add("api-key", settings.Key);
        request.Content = JsonContent.Create(new DecisionRequest(
            settings.Deployment!,
            state,
            new Dictionary<string, DecisionQuestion>
            {
                [Question] = new("choice", instructions, choices)
            }), options: Json);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DecisionResponse>(Json, ct);

        return body?.Answers?.GetValueOrDefault(Question) is { Choice: { } choice } answer
            ? new DecisionAnswer(choice, answer.Confidence)
            : null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DecisionRequest(
        string Model,
        object State,
        IReadOnlyDictionary<string, DecisionQuestion> Questions);

    private sealed record DecisionQuestion(
        string Type,
        string Instructions,
        IReadOnlyDictionary<string, string> Criteria);

    private sealed record DecisionResponse(IReadOnlyDictionary<string, DecisionResponseAnswer>? Answers);

    private sealed record DecisionResponseAnswer(string? Choice, double Confidence);
}
