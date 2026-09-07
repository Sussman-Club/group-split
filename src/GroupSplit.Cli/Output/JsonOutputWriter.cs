using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GroupSplit.Cli.Infrastructure;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Output;

/// <summary>
/// Machine mode. One JSON document on stdout, the error envelope on stderr, nothing else
/// mixed in. The renderer handed to <see cref="Write{T}"/> is deliberately ignored.
/// </summary>
public sealed class JsonOutputWriter(OutputSettings settings, TextWriter stdout, TextWriter stderr)
    : IOutputWriter
{
    private static readonly JsonSerializerOptions Compact = Create(indented: false);
    private static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The default encoder escapes <, > and ' for safe embedding in HTML. Nothing here
        // is going into a page, and \u003C in a remediation string is just harder to read
        // -- for a person and for a model.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented
    };

    // Indented only for a terminal, which happens when someone passes --output json by
    // hand. A redirected stdout is being read by a program, and the whitespace is just
    // bytes -- or context window, when the reader is a model.
    private JsonSerializerOptions Options => Console.IsOutputRedirected ? Compact : Indented;

    public OutputSettings Settings { get; } = settings;

    public void Write<T>(T payload, Func<T, IRenderable> renderer) => WriteJson(payload);

    public void WriteMessage(string message, object? payload = null)
        => WriteJson(payload ?? new { message });

    public void WriteConfirmationRequest(ConfirmationRequest request) => WriteJson(request);

    public void WriteError(CliError error)
        => stderr.WriteLine(JsonSerializer.Serialize(error, Options));

    public void Note(string message)
    {
        if (!Settings.Quiet)
        {
            stderr.WriteLine(message);
        }
    }

    public void Warn(string message)
    {
        if (!Settings.Quiet)
        {
            stderr.WriteLine($"warning: {message}");
        }
    }

    private void WriteJson<T>(T payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, Options);
        stdout.WriteLine(Project(node)?.ToJsonString(Options) ?? "null");
    }

    /// <summary>
    /// Applies --fields. Narrowing the document at the source rather than piping through
    /// jq matters for the caller that cannot pipe: an agent pays for every token of a
    /// response it only wanted two fields of.
    /// </summary>
    private JsonNode? Project(JsonNode? node)
    {
        if (Settings.Fields is not { Count: > 0 } fields || node is null)
        {
            return node;
        }

        return node switch
        {
            JsonArray array => new JsonArray(array.Select(item => Project(item?.DeepClone())).ToArray()),
            JsonObject obj => Pick(obj, fields),
            _ => node
        };
    }

    private static JsonNode Pick(JsonObject source, IReadOnlyList<string> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            // Case-insensitive so --fields Id works as well as --fields id; the payloads
            // are camelCase but the DTO names a reader is likely to have seen are Pascal.
            var match = source.FirstOrDefault(pair =>
                string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase));

            if (match.Key is not null)
            {
                result[match.Key] = match.Value?.DeepClone();
            }
        }

        return result;
    }
}
