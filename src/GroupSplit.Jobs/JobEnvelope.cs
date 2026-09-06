using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.Jobs;

/// <summary>
/// A job on its way somewhere: what kind it is, what it says, and when it was sent.
/// </summary>
/// <remarks>
/// This is the wire format, and it is the same one whether the queue is a channel in this
/// process or a topic in somebody's cloud. Serializing even for the in-process hop looks
/// like waste and is not: it is what stops a job type that cannot survive a round trip from
/// being discovered on the day the queue changes. Whatever a job carries, it carries it
/// through JSON from the first day.
/// <para>
/// <see cref="Payload"/> is a nested object rather than an escaped string, so a message
/// sitting in a queue's console reads as itself.
/// </para>
/// </remarks>
public sealed record JobEnvelope
{
    /// <summary>The job type's <see cref="JobNameAttribute"/>.</summary>
    [JsonPropertyName("jobType")]
    public required string JobType { get; init; }

    /// <summary>The job itself.</summary>
    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    [JsonPropertyName("enqueuedAt")]
    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>
    /// The envelope as it would sit on a queue. What an SQS message body would hold.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JobSerialization.Options);

    /// <summary>
    /// The reverse, for a host that receives a raw message: a queue trigger, a function
    /// invocation.
    /// </summary>
    public static JobEnvelope FromJson(string json) =>
        JsonSerializer.Deserialize<JobEnvelope>(json, JobSerialization.Options)
        ?? throw new JsonException("A job envelope deserialized to null.");
}

/// <summary>
/// One serializer for jobs, so every queue and every host agrees about the wire.
/// </summary>
public static class JobSerialization
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
