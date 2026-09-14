using System.Runtime.CompilerServices;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// A seeded bill, divided by the same code the app divides one with.
/// </summary>
/// <remarks>
/// Everything else about the transaction seeder is tested without a division -- the dating
/// on its own, the seed file's arithmetic on its own -- and the run kept failing in the gap
/// between them. The seeder builds an expense in memory and divides it before adding it, so
/// its group is only on the navigation while the FK is still null; a division that read the
/// FK saw a group-less expense and refused every item rule on the bill. Nothing but running
/// a seeded bill through the splitter says whether that path works.
/// </remarks>
public class SeededItemizedExpenseTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Payer = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CategoryId = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task A_seeded_bill_divides_by_the_rule_on_each_line()
    {
        await using var services = Composed();
        var db = services.GetRequiredService<AppDbContext>();
        Populate(db);

        await Seeder(services, Bill()).SeedAsync(Ct);

        var expense = db.ChangeTracker.Entries<Expense>().Select(entry => entry.Entity).Single();
        Assert.Equal(30m, expense.Splits.Sum(split => split.Amount));
        // Ten for the wine, which only the other person had; twenty for the pizza, which
        // the payer had on their own.
        Assert.Equal(20m, expense.Splits.Single(split => split.UserId == Payer).Amount);
        Assert.Equal(10m, expense.Splits.Single(split => split.UserId == Other).Amount);
        Assert.IsType<ItemizedSplitRuleVersion>(expense.SplitRuleVersion);
    }

    /// <summary>
    /// The bill's own arithmetic is the seeder's to refuse, and it says so by failing the
    /// run: a demo database holding an expense whose lines do not add up to it is worse
    /// than one that stopped and named the entry.
    /// </summary>
    [Fact]
    public async Task A_bill_whose_lines_do_not_add_up_stops_the_run()
    {
        await using var services = Composed();
        Populate(services.GetRequiredService<AppDbContext>());

        // Two of tax the lines do not carry: the bill no longer describes its expense.
        var bill = Bill();
        var doesNotAddUp = new TransactionSeedDto
        {
            Id = bill.Id, PayerId = bill.PayerId, CategoryId = bill.CategoryId, Amount = bill.Amount,
            Name = bill.Name, DateTime = bill.DateTime,
            Receipt = new ReceiptSeedDto { Tax = 2m, Items = bill.Receipt!.Items }
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Seeder(services, doesNotAddUp).SeedAsync(Ct));
    }

    /// <summary>The registrations <c>Program.cs</c> makes for the division, and nothing else.</summary>
    private static ServiceProvider Composed()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase($"seeder-{Guid.NewGuid()}"));
        services.AddSplitRuleServices();
        services.AddScoped<IExpenseSplitter, ExpenseSplitter>();
        services.AddScoped<ISplitRuleRevisions, SplitRuleRevisions>();
        services.AddScoped<IGroupParticipants, GroupParticipants>();
        return services.BuildServiceProvider();
    }

    private static TransactionSeeder Seeder(IServiceProvider services, TransactionSeedDto dto) =>
        new(services.GetRequiredService<AppDbContext>(),
            NullLogger<TransactionSeeder>.Instance,
            services.GetRequiredService<IExpenseSplitter>(),
            services.GetRequiredService<ISplitRuleFactory>(),
            TimeProvider.System,
            new FakeSource([dto]));

    /// <summary>A group of two, and a category that divides by whatever the bill says.</summary>
    private static void Populate(AppDbContext db)
    {
        var group = new Group { Name = "Flat", Currency = Currencies.Default };
        var payer = new User { Id = Payer, FirstName = "Ana", LastName = "B", Email = "ana@example.com" };
        var other = new User { Id = Other, FirstName = "Dan", LastName = "C", Email = "dan@example.com" };
        payer.Groups.Add(group);
        other.Groups.Add(group);
        var itemized = new ItemizedSplitRuleVersion { StartedAt = DateTimeOffset.UnixEpoch };
        var rule = new SplitRule { Name = "By items", Group = group, Versions = { itemized } };
        itemized.SplitRule = rule;
        db.AddRange(group, payer, other, rule,
            new Category { Id = CategoryId, Name = "Food", Group = group, DefaultSplitRule = rule });
        db.SaveChanges();
    }

    private static TransactionSeedDto Bill() => new()
    {
        Id = Guid.NewGuid(),
        PayerId = Payer,
        CategoryId = CategoryId,
        Amount = 30m,
        Name = "Dinner",
        DateTime = new DateTimeOffset(2026, 3, 14, 21, 15, 0, TimeSpan.Zero),
        Receipt = new ReceiptSeedDto
        {
            Items =
            [
                new ReceiptItemSeedDto { Name = "Pizza", Price = 20m, RuleName = "Items: Ana",
                    SplitRule = new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [Payer] = 1 } } },
                new ReceiptItemSeedDto { Name = "Wine", Price = 10m, RuleName = "Items: Dan",
                    SplitRule = new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [Other] = 1 } } }
            ]
        }
    };

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
