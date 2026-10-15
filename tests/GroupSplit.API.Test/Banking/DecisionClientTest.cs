using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.Banking.Suggestions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The wire call to Microsoft-Decision-1, and where its endpoint and key come from. The
/// request shape is the one the live service answered when this was written; the
/// configuration is the receipt connection's, so one switch turns all of Azure off.
/// </summary>
public class DecisionClientTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Asks_the_decision_route_of_the_resource_with_the_deployment_as_the_model()
    {
        var handler = new Recording("""
            {"model":"microsoft-decision-1","answers":{"filing":{"choice":"Home · Groceries","confidence":0.88,
             "probabilities":{"Home · Groceries":0.91,"cannot tell":0.09},"type":"choice"}},
             "usage":{"input_tokens":122,"output_tokens":1}}
            """);
        var client = Client(handler);

        var answer = await client.ChooseAsync(
            new { merchant = "Lidl" },
            "Which one?",
            new Dictionary<string, string> { ["Home · Groceries"] = "food", ["cannot tell"] = "no idea" },
            Ct);

        Assert.Equal(new DecisionAnswer("Home · Groceries", 0.88), answer);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://resource.openai.azure.com/providers/microsoft/v1/systemone", handler.Uri?.ToString());
        Assert.Equal("test-key", handler.ApiKey);

        // The live service answers 400 no_content_length_header to a streamed body.
        Assert.NotNull(handler.ContentLength);
        Assert.Equal(handler.ContentLength, Encoding.UTF8.GetByteCount(handler.Body!));

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Decision-1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Lidl", body.RootElement.GetProperty("state").GetProperty("merchant").GetString());

        var question = body.RootElement.GetProperty("questions").GetProperty("filing");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Which one?", question.GetProperty("instructions").GetString());
        Assert.Equal("food", question.GetProperty("criteria").GetProperty("Home · Groceries").GetString());
    }

    private const string Answered = """
        {"model":"microsoft-decision-1","answers":{"filing":{"choice":"Home · Groceries","confidence":0.88,
         "probabilities":{"Home · Groceries":0.91,"cannot tell":0.09},"type":"choice"}},
         "usage":{"input_tokens":122,"output_tokens":1}}
        """;

    private static readonly Dictionary<string, string> TwoOptions = new()
    {
        ["Home · Groceries"] = "food",
        ["cannot tell"] = "no idea"
    };

    [Fact]
    public async Task Each_call_is_a_GenAI_client_span()
    {
        using var spans = new Spans();

        await Client(new Recording(Answered)).ChooseAsync(new { merchant = "Lidl" }, "Which one?", TwoOptions, Ct);

        var span = Assert.Single(spans.Stopped);

        Assert.Equal("decide Decision-1", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("decide", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("azure.ai.inference", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("Decision-1", span.GetTagItem("gen_ai.request.model"));
        Assert.Equal("microsoft-decision-1", span.GetTagItem("gen_ai.response.model"));
        Assert.Equal(122, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(1, span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal("resource.openai.azure.com", span.GetTagItem("server.address"));
        Assert.Equal("Home · Groceries", span.GetTagItem("groupsplit.decision.choice"));
        Assert.Equal(0.88, span.GetTagItem("groupsplit.decision.confidence"));
        Assert.Equal(2, span.GetTagItem("groupsplit.decision.options"));
    }

    [Fact]
    public async Task What_was_asked_stays_off_the_span_unless_message_content_is_switched_on()
    {
        using var spans = new Spans();

        await Client(new Recording(Answered)).ChooseAsync(new { merchant = "Lidl" }, "Which one?", TwoOptions, Ct);

        var span = Assert.Single(spans.Stopped);

        Assert.Null(span.GetTagItem("gen_ai.input.messages"));
        Assert.Null(span.GetTagItem("gen_ai.output.messages"));
        Assert.Null(span.GetTagItem("gen_ai.system_instructions"));
    }

    [Fact]
    public async Task With_message_content_on_the_span_carries_the_question_and_the_answer()
    {
        using var spans = new Spans();

        await Client(new Recording(Answered), captureMessageContent: true)
            .ChooseAsync(new { merchant = "Lidl" }, "Which one?", TwoOptions, Ct);

        var span = Assert.Single(spans.Stopped);

        Assert.Contains("Lidl", (string)span.GetTagItem("gen_ai.input.messages")!);
        Assert.Contains("Which one?", (string)span.GetTagItem("gen_ai.system_instructions")!);

        using var output = JsonDocument.Parse((string)span.GetTagItem("gen_ai.output.messages")!);
        var message = output.RootElement[0];
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal("Home · Groceries (0.88)", message.GetProperty("parts")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_refused_call_is_an_error_span_with_the_status_code()
    {
        using var spans = new Spans();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(new Recording("""{"error":{"code":"DeploymentNotFound"}}""", HttpStatusCode.NotFound))
                .ChooseAsync(new { }, "Which one?", TwoOptions, Ct));

        var span = Assert.Single(spans.Stopped);

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("404", span.GetTagItem("error.type"));
    }

    [Fact]
    public void Message_content_follows_the_GenAI_switch()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Enabled"] = "true",
            ["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"] = "true",
            ["ConnectionStrings:filing-suggestions"] =
                "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=secret;Model=Decision-1"
        });

        Assert.True(options.CaptureMessageContent);
    }

    [Fact]
    public async Task A_refusal_from_the_service_is_an_exception_the_suggester_turns_into_no_suggestion()
    {
        var client = Client(new Recording("""{"error":{"code":"DeploymentNotFound"}}""", HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChooseAsync(
            new { }, "Which one?", new Dictionary<string, string> { ["a"] = "a", ["b"] = "b" }, Ct));
    }

    [Fact]
    public void The_endpoint_key_and_deployment_come_from_the_model_reference()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Enabled"] = "true",
            ["ConnectionStrings:filing-suggestions"] =
                "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=secret;Model=Decision-1"
        });

        Assert.True(options.IsConfigured);
        Assert.Equal("Decision-1", options.Deployment);
        Assert.Equal(new Uri("https://resource.openai.azure.com/"), options.Endpoint);
        Assert.Equal("secret", options.Key);
    }

    [Fact]
    public void Switching_Azure_off_switches_suggestions_off_too()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Enabled"] = "false",
            ["ConnectionStrings:filing-suggestions"] =
                "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=secret;Model=Decision-1"
        });

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void No_deployment_leaves_suggestions_off()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Enabled"] = "true",
            ["ConnectionStrings:filing-suggestions"] =
                "Endpoint=https://resource.openai.azure.com/openai/v1/;Key=secret;Model="
        });

        Assert.False(options.IsConfigured);
    }

    private static FilingSuggestionOptions Options(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        using var provider = new ServiceCollection()
            .AddFilingSuggestions(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<FilingSuggestionOptions>>().Value;
    }

    private static DecisionClient Client(HttpMessageHandler handler, bool captureMessageContent = false) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new FilingSuggestionOptions
        {
            Deployment = "Decision-1",
            Endpoint = new Uri("https://resource.openai.azure.com/"),
            Key = "test-key",
            CaptureMessageContent = captureMessageContent
        }));

    /// <summary>
    /// The decision spans this test's own thread finishes. Tests run in parallel and the
    /// source is static, so a listener sees every test's spans; keeping only this async
    /// flow's leaves each test its own.
    /// </summary>
    private sealed class Spans : IDisposable
    {
        private static readonly AsyncLocal<Spans?> Current = new();
        private readonly ActivityListener _listener;

        public List<Activity> Stopped { get; } = [];

        public Spans()
        {
            Current.Value = this;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DecisionClient.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Current.Value is { } spans)
                        lock (spans.Stopped)
                            spans.Stopped.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class Recording(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
            // Before reading, which would buffer it and make up a length the wire never had.
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
