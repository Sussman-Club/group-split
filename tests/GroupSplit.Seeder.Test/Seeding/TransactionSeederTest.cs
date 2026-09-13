using System.Runtime.CompilerServices;
using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
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

    /// <summary>
    /// A seeded bill is saved, not just divided by.
    /// </summary>
    /// <remarks>
    /// <see cref="Expense.Bill"/> is not a mapped navigation -- it is how the splitter is
    /// handed the whole receipt -- so a bill attached while mapping divides the expense and
    /// then vanishes unless the seeder adds it. Nothing about the demo looks wrong when that
    /// happens: the balances are itemised and correct, and every bill behind them is missing,
    /// which is only visible as an expense whose bill section never appears.
    /// </remarks>
    [Fact]
    public void A_seeded_bill_is_saved_beside_its_expense()
    {
        var db = Context();
        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            Amount = 40m,
            Currency = Currencies.Default,
            Name = "Dinner",
            DateTime = Now,
            Bill = SeededBill()
        };

        Added(db, expense);

        var bill = Assert.Single(db.ChangeTracker.Entries<Receipt>().Select(entry => entry.Entity));

        Assert.Equal(40m, bill.Total);
        Assert.All(bill.Items, line => Assert.Equal(expense.Id, line.ExpenseId));
    }

    /// <summary>An expense with no bill -- which is nearly all of them -- adds none.</summary>
    [Fact]
    public void An_expense_with_no_bill_adds_none()
    {
        var db = Context();

        Added(db, new Expense
        {
            Id = Guid.NewGuid(),
            Amount = 14.02m,
            Currency = Currencies.Default,
            Name = "Coffee",
            DateTime = Now
        });

        Assert.Empty(db.ChangeTracker.Entries<Receipt>());
        Assert.Single(db.ChangeTracker.Entries<Expense>());
    }

    /// <summary>
    /// A bill on an expense that divides some other way may not say who had what.
    /// </summary>
    /// <remarks>
    /// The itemised rule is the only thing that reads a claim. Under any other rule the
    /// shares come from somewhere else entirely, and the claims would sit on the slip as an
    /// answer to a question nothing asked -- so the run stops and names the lines rather than
    /// seed a demo that says more than the app does.
    /// </remarks>
    [Fact]
    public async Task A_bill_on_an_expense_that_divides_some_other_way_may_not_say_who_had_what()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Mapped(dividesByItsBill: false));

        Assert.Contains("Bistecca", refusal.Message);
    }

    /// <summary>And under the rule that does read them, the same file seeds them.</summary>
    [Fact]
    public async Task A_bill_on_an_expense_split_by_its_bill_keeps_who_had_what()
    {
        var expense = await Mapped(dividesByItsBill: true);

        var line = Assert.Single(expense!.Bill!.Items);

        Assert.Equal(Anabel, Assert.Single(line.Claims).UserId);
    }

    private static readonly Guid Anabel = Guid.Parse("94F67FC0-F549-46ED-AD81-D6DF79225396");

    /// <summary>
    /// One seeded expense with a claimed bill, mapped as the seeder maps it -- under a rule
    /// that divides by the bill, or one that does not.
    /// </summary>
    private static async Task<Expense?> Mapped(bool dividesByItsBill)
    {
        var db = Context();

        db.Set<User>().Add(new User { Id = Anabel, FirstName = "Anabel", Email = "anabel@test.com" });
        await db.SaveChangesAsync();

        return await new Probe(db, new FixedClock(Now), new FakeSource([]),
                new FakeSplitter(dividesByItsBill))
            .Map(new TransactionSeedDto
            {
                Id = Guid.NewGuid(),
                PayerId = Anabel,
                Amount = 40m,
                Name = "Dinner",
                DateTime = Now,
                Receipt = new ReceiptSeedDto
                {
                    Items =
                    [
                        new ReceiptItemSeedDto
                        {
                            Name = "Bistecca", Price = 40m,
                            Had = new Dictionary<Guid, int> { [Anabel] = 1 }
                        }
                    ]
                }
            });
    }

    private static Receipt SeededBill()
    {
        var receipt = new Receipt { Subtotal = 40m, Total = 40m };

        receipt.Items.Add(new ReceiptItem
        {
            Name = "Bistecca", NormalizedName = "bistecca", TotalPrice = 40m
        });

        return receipt;
    }

    /// <summary>
    /// A context nothing ever saves: what is under test is what the seeder adds, so these
    /// read the change tracker. The bill's own lines carry the expense's id, which is what
    /// ties them together once it is saved for real.
    /// </summary>
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Added(AppDbContext db, Expense expense)
    {
        foreach (var line in expense.Bill?.Items ?? [])
            line.ExpenseId = expense.Id;

        new Probe(db, new FixedClock(Now), new FakeSource([]))
            .Add(expense, new TransactionSeedDto
            {
                Id = expense.Id, PayerId = Guid.NewGuid(), Amount = expense.Amount,
                Name = expense.Name, DateTime = Now
            });
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
    private sealed class Probe(
        AppDbContext db,
        TimeProvider clock,
        ISeedDataSource<TransactionSeedDto> source,
        IExpenseSplitter? splitter = null)
        : TransactionSeeder(db, NullLogger<TransactionSeeder>.Instance, splitter!, clock, source)
    {
        public DateTimeOffset On(TransactionSeedDto dto) => When(dto);

        public void Add(Expense expense, TransactionSeedDto dto) =>
            AddEntityAsync(expense, dto).GetAwaiter().GetResult();

        public Task<Expense?> Map(TransactionSeedDto dto) => MapAsync(dto);
    }

    /// <summary>
    /// A splitter that answers the one question mapping asks it and divides nothing.
    /// </summary>
    /// <remarks>
    /// What is under test is which bills may name who had what, and the answer turns on
    /// whether the expense divides by its bill. The division itself is
    /// <c>ExpenseSplitter</c>'s, and is tested where it lives.
    /// </remarks>
    private sealed class FakeSplitter(bool dividesByItsBill) : IExpenseSplitter
    {
        public Task WriteSplitsAsync(Expense expense, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task WriteSplitsAsync(
            Expense expense, IReadOnlyList<SplitInput>? given, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DividesByItsBill(Expense expense, CancellationToken ct = default) =>
            Task.FromResult(dividesByItsBill);

        public Task<bool> DivisionCameFromItsRule(Expense expense, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
