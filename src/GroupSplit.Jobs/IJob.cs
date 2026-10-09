namespace GroupSplit.Jobs;

/// <summary>A request for work, carried to whichever handler is registered for its type.</summary>
/// <remarks>
/// A job that fails is logged with its <see cref="object.ToString"/> as its inputs, which for
/// a record is every property. Keep jobs to ids and plain values; one that has to carry a
/// secret overrides <c>ToString</c> -- or, on a record, <c>PrintMembers</c> -- to leave it out.
/// </remarks>
public interface IJob;

public interface IJob<TResult> : IJob;
