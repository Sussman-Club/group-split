using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.AppHost.Test.Data;

public class DataTest(AppHostFixture appHost) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await appHost.SeedAsync();
    }

    /// <summary>
    /// Postgres stores <c>timestamptz</c> as an instant, so a transaction written from a
    /// non-UTC offset has to come back as the same moment in time. The in-memory provider
    /// the API tests use round-trips the offset verbatim and would pass either way, which
    /// is the whole reason this test needs a real database.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_transaction_written_with_an_offset_comes_back_as_the_same_instant()
    {
        await using var context = await appHost.GetDbContextAsync();

        // Rolled back on dispose, so the test leaves the seeded database as it found it.
        await using var dbTransaction =
            await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var queryResult = await (from user in context.Set<User>()

            select new
            {
                user,
                personalGroup = user.PersonalGroup
            }).FirstAsync(TestContext.Current.CancellationToken);

        var written = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8));

        var transaction = new Transaction
        {
            Amount = 100,
            DateTime = written,
            Name = "Test",
            User = queryResult.user,
            RuleVersion = new PersonalRuleVersion
            {
                Rule = new Rule
                {
                    Category = "Test",
                    Group = queryResult.personalGroup
                },
                StartDateTime = DateTime.UtcNow
            }
        };

        context.Add(transaction);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Read back through a fresh query rather than off the tracked entity, which would
        // hand back the value already in memory and never touch the column.
        var stored = await context.Set<Transaction>()
            .AsNoTracking()
            .Where(candidate => candidate.Id == transaction.Id)
            .Select(candidate => candidate.DateTime)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(written, stored);
        Assert.Equal(written.UtcDateTime, stored.UtcDateTime);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
