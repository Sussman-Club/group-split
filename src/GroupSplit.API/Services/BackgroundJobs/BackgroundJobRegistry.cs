using System.Text.Json;
using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Explicit, stable wire names; database contents cannot select arbitrary CLR types.</summary>
public sealed class BackgroundJobRegistry
{
    private readonly Dictionary<(string, int), Type> _types = new();
    private readonly Dictionary<Type, (string Name, int Version)> _names = new();

    public BackgroundJobRegistry Add<TJob>(string name, int version = 1) where TJob : IJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || version < 1) throw new ArgumentOutOfRangeException(nameof(name));
        _types.Add((name, version), typeof(TJob));
        _names.Add(typeof(TJob), (name, version));
        return this;
    }

    internal (string Name, int Version, string Payload) Serialize(IJob job)
    {
        var type = job.GetType();
        if (!_names.TryGetValue(type, out var wire))
            throw new InvalidOperationException("Job type is not registered for durable execution.");
        return (wire.Name, wire.Version, JsonSerializer.Serialize(job, type));
    }

    internal IJob Deserialize(string name, int version, string payload)
    {
        if (!_types.TryGetValue((name, version), out var type))
            throw new InvalidOperationException("Unknown durable job contract.");
        return (IJob)(JsonSerializer.Deserialize(payload, type)
            ?? throw new JsonException("Missing durable job payload."));
    }
}
