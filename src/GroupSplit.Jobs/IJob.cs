namespace GroupSplit.Jobs;

/// <summary>
/// Marks a type as something that can be put on a queue and handled later.
/// </summary>
/// <remarks>
/// It declares nothing, and it is not decoration. Without it <c>EnqueueAsync</c> is generic
/// over anything at all, so enqueuing the wrong object -- a registration, a response, an
/// entity -- compiles perfectly and fails at run time, inside a background worker, as a log
/// line nobody reads. That is not hypothetical: the recurring scheduler did exactly that
/// while this was being written, and a daily bank sweep quietly enqueued nothing.
/// <para>
/// With it, the compiler asks the question instead. A job also needs a
/// <see cref="JobNameAttribute"/>, which is checked when it is registered.
/// </para>
/// </remarks>
public interface IJob;
