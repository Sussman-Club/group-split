using System.Reflection;
using System.Text.Json;

namespace GroupSplit.Jobs;

/// <summary>
/// Which job types exist, what each is called on the wire, and how to hand one to its
/// handler.
/// </summary>
/// <remarks>
/// Built once at registration, read-only afterwards. Holding the invoker here rather than
/// making it at dispatch is what keeps reflection out of the running path: at registration
/// the job's type is a type argument, so a closed <see cref="JobInvoker{TJob}"/> can simply
/// be constructed.
/// </remarks>
public sealed class JobRegistry
{
    private readonly Dictionary<string, JobKind> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, JobKind> _byType = [];

    /// <summary>Every job type registered, for a host that wants to log what it can handle.</summary>
    public IEnumerable<string> Names => _byName.Keys;

    internal void Register<TJob>() where TJob : IJob
    {
        var name = NameOf(typeof(TJob));
        var kind = new JobKind(name, typeof(TJob), new JobInvoker<TJob>());

        if (_byName.TryGetValue(name, out var taken) && taken.Type != typeof(TJob))
        {
            throw new InvalidOperationException(
                $"Job name \"{name}\" is already taken by {taken.Type.Name}; it cannot also name {typeof(TJob).Name}.");
        }

        _byName[name] = kind;
        _byType[typeof(TJob)] = kind;
    }

    /// <summary>
    /// The wire name for a job type, from its <see cref="JobNameAttribute"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type does not carry one.</exception>
    public static string NameOf(Type jobType) =>
        jobType.GetCustomAttribute<JobNameAttribute>()?.Name
        ?? throw new InvalidOperationException(
            $"{jobType.Name} is used as a job but carries no [JobName]. Give it one; it is the name it travels under.");

    /// <summary>Wraps a job in the envelope it travels in.</summary>
    public JobEnvelope Envelope<TJob>(TJob job, DateTimeOffset enqueuedAt) where TJob : IJob
    {
        if (!_byType.ContainsKey(typeof(TJob)))
        {
            throw new InvalidOperationException(
                $"{typeof(TJob).Name} was enqueued but is not registered. Add services.AddJob<{typeof(TJob).Name}, ...>().");
        }

        return new JobEnvelope
        {
            JobType = NameOf(typeof(TJob)),
            Payload = JsonSerializer.SerializeToElement(job, JobSerialization.Options),
            EnqueuedAt = enqueuedAt
        };
    }

    /// <summary>
    /// What arrived, ready to run, or null for a name this host does not know -- an older
    /// message, or one meant for a different deployment.
    /// </summary>
    internal JobKind? Find(string jobName) => _byName.GetValueOrDefault(jobName);

    internal sealed record JobKind(string Name, Type Type, IJobInvoker Invoker);
}
