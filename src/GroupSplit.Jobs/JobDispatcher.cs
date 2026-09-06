using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs;

/// <summary>
/// Runs one job that has arrived from somewhere.
/// </summary>
/// <remarks>
/// The entry point every host shares, and the reason a different transport is a small
/// change: an in-process pump reading a channel and a function invoked with a queue message
/// both end here, having done nothing but produce a <see cref="JobEnvelope"/>.
/// </remarks>
public interface IJobDispatcher
{
    /// <summary>
    /// Runs the job the envelope holds, in a scope of its own.
    /// </summary>
    /// <returns>
    /// Whether anything ran. False means this host has no handler for that name, which is
    /// not a failure: a message can outlive the deployment that understood it.
    /// </returns>
    /// <exception cref="Exception">Whatever the handler threw. The job did not happen.</exception>
    Task<bool> DispatchAsync(JobEnvelope envelope, CancellationToken ct = default);
}

internal sealed class JobDispatcher(
    JobRegistry registry,
    IServiceScopeFactory scopes,
    ILogger<JobDispatcher> logger) : IJobDispatcher
{
    public async Task<bool> DispatchAsync(JobEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (registry.Find(envelope.JobType) is not { } kind)
        {
            logger.LogWarning(
                "A job named \"{JobType}\" arrived and nothing here handles it; ignored. Known: {Known}.",
                envelope.JobType, string.Join(", ", registry.Names));
            return false;
        }

        // A scope per job, so a handler may take the same scoped services an endpoint
        // would, and so one job's DbContext is never another's.
        using var scope = scopes.CreateScope();

        await kind.Invoker.InvokeAsync(scope.ServiceProvider, envelope.Payload, ct).ConfigureAwait(false);

        return true;
    }
}
