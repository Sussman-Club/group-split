using EntityFramework.Exceptions.Common;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Errors;

/// <summary>
/// A save that can lose a race says which, and is answered with that race's refusal; any
/// other failure is thrown as it is, and is a 500 like any other bug.
/// </summary>
/// <remarks>
/// The unique indexes are told apart by the name the exception processor reads off the
/// model, which only it can set, and the in-memory provider has no indexes to violate:
/// that half is <c>DoubleSubmitTest</c>'s, on Postgres. What the in-memory provider does
/// have is a delete of a row that is not there, which fails the way a delete racing
/// another delete does.
/// </remarks>
public class LostRaceTest : IAsyncLifetime
{
    private readonly AppDbContext _context = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"LostRace_{Guid.NewGuid()}")
        .Options);

    private static readonly LostRace NameRace = LostRace.OnUniqueIndex("IX_Category_GroupId_Name",
        () => new ConflictException(ErrorCodes.CategoryNameTaken, "Taken."));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public async Task A_save_that_finds_its_row_gone_is_a_concurrent_change_when_it_says_it_can_be()
    {
        DeleteARowThatIsNotThere();

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            _context.SaveChangesAsync(Ct, NameRace, LostRace.ChangedMeanwhile));

        Assert.Equal(ErrorCodes.ConcurrentChange, refusal.Code);
    }

    [Fact]
    public async Task A_save_that_does_not_say_it_can_find_its_row_gone_throws_what_the_database_said()
    {
        DeleteARowThatIsNotThere();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _context.SaveChangesAsync(Ct, NameRace));
    }

    [Fact]
    public void A_unique_violation_is_answered_only_for_an_index_the_save_names()
    {
        // No name: the processor could not match the index to the model, so nothing here can
        // say which check it backs.
        Assert.Null(LostRace.Refusal(new UniqueConstraintException("Unique constraint violation."),
            [NameRace, LostRace.ChangedMeanwhile]));
    }

    [Fact]
    public void A_concurrency_failure_is_not_answered_by_a_unique_index_race()
    {
        Assert.Null(LostRace.Refusal(new DbUpdateConcurrencyException("Gone."), [NameRace]));
    }

    [Fact]
    public void Any_other_failed_save_is_none_of_them()
    {
        Assert.Null(LostRace.Refusal(new DbUpdateException("Something else."),
            [NameRace, LostRace.ChangedMeanwhile]));
    }

    [Fact]
    public void Each_refusal_is_a_new_exception()
    {
        var first = LostRace.Refusal(new DbUpdateConcurrencyException("Gone."), [LostRace.ChangedMeanwhile]);
        var second = LostRace.Refusal(new DbUpdateConcurrencyException("Gone."), [LostRace.ChangedMeanwhile]);

        Assert.NotNull(first);
        Assert.NotSame(first, second);
    }

    private void DeleteARowThatIsNotThere() =>
        _context.Entry(new Category { Name = "Food" }).State = EntityState.Deleted;
}
