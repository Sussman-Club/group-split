using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs;

/// <summary>
/// Turns an envelope back into a job and gives it to its handler, without the caller ever
/// naming either type.
/// </summary>
/// <remarks>
/// The bridge from a world of strings and JSON back into a typed one. It exists so
/// <see cref="JobDispatcher"/> can stay non-generic: a dispatcher holding a job type as a
/// <see cref="Type"/> would otherwise need reflection to call a generic handler, once per
/// job, forever.
/// </remarks>
internal interface IJobInvoker
{
    Task InvokeAsync(IServiceProvider scope, JsonElement payload, CancellationToken ct);
}

internal sealed class JobInvoker<TJob> : IJobInvoker where TJob : IJob
{
    public Task InvokeAsync(IServiceProvider scope, JsonElement payload, CancellationToken ct)
    {
        var job = payload.Deserialize<TJob>(JobSerialization.Options)
                  ?? throw new JsonException($"A {typeof(TJob).Name} payload deserialized to null.");

        return scope.GetRequiredService<IJobHandler<TJob>>().HandleAsync(job, ct);
    }
}
