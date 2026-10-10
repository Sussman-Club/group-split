using EntityFramework.Exceptions.Common;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// A race the database can settle during one save, and the refusal the request that lost it
/// is given.
/// </summary>
/// <remarks>
/// The services check before they write, and say which name or which row in their own words.
/// Two requests can both pass that check, and the database is then the only thing standing
/// between them: a unique index refusing the second of two inserts, or an update or delete
/// finding its row changed or gone. Each save that can lose one of those says which, so that
/// its answer is the answer the check gives -- and a refusal it does not name is a write the
/// code never meant to make, which is a bug and stays a 500.
/// <para>
/// The unique ones are told apart by index name, which <c>UseExceptionProcessor</c> puts on
/// the <see cref="UniqueConstraintException"/> a failed save throws.
/// </para>
/// </remarks>
internal sealed class LostRace
{
    private readonly string? _uniqueIndex;
    private readonly Func<DomainException> _refusal;

    private LostRace(string? uniqueIndex, Func<DomainException> refusal)
    {
        _uniqueIndex = uniqueIndex;
        _refusal = refusal;
    }

    /// <summary>
    /// The unique index <paramref name="index"/> refusing a row another request wrote first,
    /// answered with <paramref name="refusal"/>: the refusal of the check the index backs.
    /// </summary>
    public static LostRace OnUniqueIndex(string index, Func<DomainException> refusal) => new(index, refusal);

    /// <summary>
    /// An update or delete finding its row changed or gone: the concurrency token said
    /// something else, or another request deleted it first. Nothing was saved.
    /// </summary>
    public static LostRace ChangedMeanwhile { get; } = new(null, () =>
        new ConflictException(ErrorCodes.ConcurrentChange,
            "Somebody else changed this at the same moment. Nothing was saved; look again before retrying."));

    /// <summary>
    /// The refusal a failed save amounts to, out of the races it was expected to lose; null
    /// when it is none of them.
    /// </summary>
    public static DomainException? Refusal(DbUpdateException exception, IEnumerable<LostRace> races)
    {
        var lost = exception switch
        {
            DbUpdateConcurrencyException => races.FirstOrDefault(race => race._uniqueIndex is null),
            UniqueConstraintException { ConstraintName: { } index } =>
                races.FirstOrDefault(race => string.Equals(race._uniqueIndex, index, StringComparison.Ordinal)),
            _ => null
        };

        return lost?._refusal();
    }
}

internal static class LostRaces
{
    extension(AppDbContext context)
    {
        /// <summary>
        /// Saves, answering each of <paramref name="races"/> with its refusal if this save is
        /// the one that lost it. Anything else the database refuses is thrown as it is.
        /// </summary>
        public async Task<int> SaveChangesAsync(CancellationToken ct, params LostRace[] races)
        {
            try
            {
                return await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (LostRace.Refusal(exception, races) is { } refusal)
            {
                throw refusal;
            }
        }
    }
}
