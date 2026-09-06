using System.Net;
using System.Text;
using Going.Plaid;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.API.Test.Banking.Plaid;

/// <summary>
/// A Plaid that answers from recorded payloads.
/// </summary>
/// <remarks>
/// The connector is the one class that speaks Plaid's vocabulary, so it is the one class
/// that cannot be tested through the seam. It is tested here instead: real payloads in, the
/// app's own records out, with nothing between them but the code under test and Going.Plaid
/// deserializing exactly what the real service would send.
/// <para>
/// A hand-written <see cref="HttpMessageHandler"/> rather than a mocking library, which is
/// what the seeder's Keycloak client already does.
/// </para>
/// </remarks>
internal sealed class PlaidTestServer : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _answers = new(StringComparer.Ordinal);

    /// <summary>Every request that was made, in order, as (path, body).</summary>
    public List<(string Path, string Body)> Requests { get; } = [];

    /// <summary>Queues one answer for a path. Answers are used in the order they were added.</summary>
    public PlaidTestServer Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (!_answers.TryGetValue(path, out var queue))
            _answers[path] = queue = new Queue<(HttpStatusCode, string)>();

        queue.Enqueue((status, body));

        return this;
    }

    /// <summary>Queues an answer read from <c>Banking/Plaid/Payloads</c>.</summary>
    public PlaidTestServer AnswerWith(string path, string payloadFile, HttpStatusCode status = HttpStatusCode.OK) =>
        Answer(path, Payload(payloadFile), status);

    public static string Payload(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Banking", "Plaid", "Payloads", fileName));

    /// <summary>A client that talks to this server and nothing else.</summary>
    public PlaidClient Client() =>
        new(Going.Plaid.Environment.Sandbox,
            clientId: "test-client",
            secret: "test-secret",
            accessToken: null,
            httpClientFactory: new SingleClientFactory(this),
            logger: NullLogger<PlaidClient>.Instance);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add((path, body));

        if (!_answers.TryGetValue(path, out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"No recorded answer for {path}.");

        var (status, payload) = queue.Dequeue();

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SingleClientFactory(PlaidTestServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(server, disposeHandler: false);
    }
}
