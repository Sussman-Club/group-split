using System.Runtime.CompilerServices;
using GroupSplit.Data;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// The day a seeded expense is recorded against.
/// </summary>
/// <remarks>
/// Two kinds of seeded expense want two different answers. The history wants fixed dates --
/// three years of them, on the days things happened. The handful that have to sit beside a
/// seeded bank row want a relative one, because those rows are dated from the run so that a
/// database seeded months ago still reads as this week's. An expense on a fixed date stops
/// being the same money as one of them after a week, and a demo where nothing is ever a
/// duplicate cannot show the one thing the inbox is guarding against.
/// </remarks>
public class TransactionSeederTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_expense_with_a_date_is_recorded_on_it()
    {
        var on = new DateTimeOffset(2025, 3, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(on, When(new() { Id = Guid.NewGuid(), PayerId = Guid.NewGuid(), Amount = 14.02m, Name = "Coffee", DateTime = on }));
    }

    [Fact]
    public void An_expense_counted_back_from_the_run_lands_on_that_day()
    {
        var when = When(new()
        {
            Id = Guid.NewGuid(),
            PayerId = Guid.NewGuid(),
            Amount = 40m,
            Name = "Dinner",
            DaysAgo = 9
        });

        Assert.Equal(new DateOnly(2026, 8, 31), DateOnly.FromDateTime(when.UtcDateTime));

        // Evening rather than midnight, because a person records an expense at a moment and
        // the ledger sorts on it.
        Assert.Equal(new TimeOnly(20, 30), TimeOnly.FromDateTime(when.UtcDateTime));
    }

    /// <summary>
    /// A relative date wins. Both together is a seed entry saying two things, and the
    /// relative one is the reason it was written.
    /// </summary>
    [Fact]
    public void Counting_back_from_the_run_beats_a_date_given_as_well()
    {
        var when = When(new()
        {
            Id = Guid.NewGuid(),
            PayerId = Guid.NewGuid(),
            Amount = 40m,
            Name = "Dinner",
            DateTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DaysAgo = 1
        });

        Assert.Equal(new DateOnly(2026, 9, 8), DateOnly.FromDateTime(when.UtcDateTime));
    }

    /// <summary>
    /// Neither is a seed file that does not say when, and a silent fallback to the run's own
    /// date would put an expense in the demo's history that nobody wrote.
    /// </summary>
    [Fact]
    public void An_expense_that_says_neither_is_refused_rather_than_guessed_at()
    {
        var dto = new TransactionSeedDto
        {
            Id = Guid.NewGuid(),
            PayerId = Guid.NewGuid(),
            Amount = 40m,
            Name = "Dinner"
        };

        var refusal = Assert.Throws<InvalidOperationException>(() => When(dto));

        Assert.Contains(dto.Id.ToString(), refusal.Message);
    }

    private static DateTimeOffset When(TransactionSeedDto dto) =>
        new Probe(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options),
            new FixedClock(Now),
            new FakeSource([dto])).On(dto);

    /// <summary>
    /// The dating on its own. Mapping an expense reads the payer, the category and the
    /// splitter; none of that is what this is about, and the context here is never connected
    /// to anything.
    /// </summary>
    private sealed class Probe(AppDbContext db, TimeProvider clock, ISeedDataSource<TransactionSeedDto> source)
        : TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance, null!, clock, source)
    {
        public DateTimeOffset On(TransactionSeedDto dto) => When(dto);
    }

    /// <summary>A clock that does not move, so a relative date is a fixed expectation.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSource(IReadOnlyList<TransactionSeedDto> items) : ISeedDataSource<TransactionSeedDto>
    {
        public async IAsyncEnumerable<TransactionSeedDto> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }
}
