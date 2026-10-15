using System.Diagnostics;
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
/// <para>
/// Each call is a GenAI client span, the way the receipt reader's chat client records its
/// own: there is no SDK here to do it, so without one the dashboard shows a bare POST. The
/// attributes follow the OpenTelemetry GenAI conventions where they have a name for the
/// thing; the answer and its confidence, which they do not, are under
/// <c>groupsplit.decision.*</c>.
/// </para>
/// </remarks>
public sealed class DecisionClient(HttpClient http, IOptions<FilingSuggestionOptions> options) : IDecisionClient
{
    /// <summary>The source the spans come from, which the tracer has to be told to listen to.</summary>
    public const string ActivitySourceName = "GroupSplit.FilingSuggestions";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private const string Question = "filing";

    /// <summary>
    /// Not one of the conventions' well-known operations, which are all about generating
    /// something; the conventions allow a system's own, and this is what the call does.
    /// </summary>
    internal const string Operation = "decide";

    /// <summary>The conventions' name for models served by Azure AI Foundry.</summary>
    internal const string Provider = "azure.ai.inference";

    public async Task<DecisionAnswer?> ChooseAsync(
        object state,
        string instructions,
        IReadOnlyDictionary<string, string> choices,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        var endpoint = new Uri(settings.Endpoint!, "providers/microsoft/v1/systemone");

        using var activity = Source.StartActivity($"{Operation} {settings.Deployment}", ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", Operation);
        activity?.SetTag("gen_ai.provider.name", Provider);
        // The name the conventions used before gen_ai.provider.name, which tools that have
        // not caught up still look for.
        activity?.SetTag("gen_ai.system", Provider);
        activity?.SetTag("gen_ai.request.model", settings.Deployment);
        activity?.SetTag("server.address", endpoint.Host);
        activity?.SetTag("server.port", endpoint.Port);
        activity?.SetTag("groupsplit.decision.options", choices.Count);

        if (settings.CaptureMessageContent && activity is { IsAllDataRequested: true })
            RecordInput(activity, state, instructions, choices);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        request.Headers.Add("api-key", settings.Key);

        // Serialized up front rather than streamed: the service refuses a body sent without a
        // Content-Length, which a streamed JsonContent never has.
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new DecisionRequest(
            settings.Deployment!,
            state,
            new Dictionary<string, DecisionQuestion>
            {
                [Question] = new("choice", instructions, choices)
            }), Json));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        try
        {
            using var response = await http.SendAsync(request, ct);

            // The service says what it refused in the body, and a bare status code in the log
            // leaves nobody able to tell a bad option list from a missing deployment.
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Decision-1 answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}",
                    inner: null,
                    response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<DecisionResponse>(Json, ct);

            activity?.SetTag("gen_ai.response.model", body?.Model);
            activity?.SetTag("gen_ai.usage.input_tokens", body?.Usage?.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", body?.Usage?.OutputTokens);

            var answer = body?.Answers?.GetValueOrDefault(Question) is { Choice: { } choice } found
                ? new DecisionAnswer(choice, found.Confidence)
                : null;

            activity?.SetTag("groupsplit.decision.choice", answer?.Choice);
            activity?.SetTag("groupsplit.decision.confidence", answer?.Confidence);

            if (settings.CaptureMessageContent && answer is not null && activity is { IsAllDataRequested: true })
                RecordOutput(activity, answer);

            return answer;
        }
        catch (Exception exception) when (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.SetTag("error.type", exception is HttpRequestException { StatusCode: { } status }
                ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : exception.GetType().FullName);
            throw;
        }
    }

    /// <summary>
    /// What the model was asked, in the conventions' message shape so the dashboard's GenAI
    /// view can show it. Only when message content is switched on: it is the row's merchant,
    /// amount and receipt, and the person's own group and category names.
    /// </summary>
    private static void RecordInput(
        Activity activity, object state, string instructions, IReadOnlyDictionary<string, string> choices)
    {
        activity.SetTag("gen_ai.system_instructions", JsonSerializer.Serialize(new[]
        {
            new MessagePart("text", instructions)
        }, Json));

        activity.SetTag("gen_ai.input.messages", JsonSerializer.Serialize(new[]
        {
            new Message("user", [
                new MessagePart("text", JsonSerializer.Serialize(state, Json)),
                new MessagePart("text", "Options:\n" + string.Join("\n", choices.Keys.Select(choice => $"- {choice}")))
            ])
        }, Json));
    }

    private static void RecordOutput(Activity activity, DecisionAnswer answer) =>
        activity.SetTag("gen_ai.output.messages", JsonSerializer.Serialize(new[]
        {
            new Message("assistant", [
                new MessagePart("text", $"{answer.Choice} ({answer.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})")
            ], "stop")
        }, Json));

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

    private sealed record DecisionResponse(
        string? Model,
        IReadOnlyDictionary<string, DecisionResponseAnswer>? Answers,
        DecisionUsage? Usage);

    private sealed record DecisionResponseAnswer(string? Choice, double Confidence);

    private sealed record DecisionUsage(
        [property: JsonPropertyName("input_tokens")] int? InputTokens,
        [property: JsonPropertyName("output_tokens")] int? OutputTokens);

    private sealed record Message(
        string Role,
        IReadOnlyList<MessagePart> Parts,
        [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

    private sealed record MessagePart(string Type, string Content);
}
