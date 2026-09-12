using System.Runtime.CompilerServices;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// The bill a seeded bank row can carry, and what it has to be before it is seeded at all.
/// </summary>
/// <remarks>
/// A bill on an unfiled row is the state the whole feature turns on -- lines typed at the
/// shop, before anybody has decided which of them are one purchase -- and it is the only
/// such state a seeder can produce, since splitting a charge goes through the inbox.
/// <para>
/// Worth its own tests because nothing else would notice it going wrong: a bill that never
/// gets added leaves an inbox that looks perfectly normal, and a bill added on every run
/// leaves rows with several, which only shows up as a total nobody can explain.
/// </para>
/// </remarks>
public class BankConnectionSeederTest
{
    private const string Anabel = "B2C1F8E1-8B2A-4E77-9A29-8F0E570A4F64";

    [Fact]
    public async Task A_row_with_a_bill_gets_one_with_the_rows_id_on_it()
    {
        var db = Context();
        var dto = Connection(Warehouse());

        await Add(db, dto);

        var bill = Assert.Single(Tracked<Receipt>(db));

        Assert.Equal(Row, bill.BankTransactionId);
        Assert.Equal(138.52m, bill.Subtotal);
        Assert.Equal(19.55m, bill.Tax);
        Assert.Equal(158.07m, bill.Total);
    }

    /// <summary>
    /// No line belongs to an expense yet, and that absence is the point: which line is whose
    /// purchase is the question splitting the charge answers.
    /// </summary>
    [Fact]
    public async Task The_lines_of_an_unfiled_bill_name_no_expense()
    {
        var db = Context();

        await Add(db, Connection(Warehouse()));

        var bill = Assert.Single(Tracked<Receipt>(db));

        Assert.Equal(3, bill.Items.Count);
        Assert.All(bill.Items, line => Assert.Null(line.ExpenseId));
    }

    /// <summary>
    /// The exemption survives, which is the only reason the field is in the seed file: tax
    /// weighed over every line would tax the groceries and let the jacket off.
    /// </summary>
    [Fact]
    public async Task An_exempt_line_stays_exempt()
    {
        var db = Context();

        await Add(db, Connection(Warehouse()));

        var bill = Assert.Single(Tracked<Receipt>(db));

        Assert.False(bill.Items.Single(line => line.Name == "Rotisserie chicken").IsTaxable);
        Assert.True(bill.Items.Single(line => line.Name == "Fleece jacket").IsTaxable);
    }

    /// <summary>
    /// A bill that is not the charge stops the run, naming the row.
    /// </summary>
    /// <remarks>
    /// Refused here rather than left for the app, because a row seeded like this looks ready
    /// and fails at the moment somebody tries to use it: filing it and splitting it both
    /// refuse a bill whose total is not the money being divided.
    /// </remarks>
    [Fact]
    public async Task A_bill_that_does_not_come_to_the_charge_is_refused_by_name()
    {
        var db = Context();
        var dto = Connection(Warehouse(charge: 200m));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Add(db, dto));

        Assert.Contains(Row.ToString(), thrown.Message);
        Assert.Contains("158.07", thrown.Message);
        Assert.Empty(Tracked<Receipt>(db));
    }

    /// <summary>An ordinary row -- which is nearly every row -- gets no bill.</summary>
    [Fact]
    public async Task A_row_with_no_bill_seeds_none()
    {
        var db = Context();

        await Add(db, Connection(Warehouse(billed: false)));

        Assert.Empty(Tracked<Receipt>(db));
        Assert.Single(Tracked<BankConnection>(db));
    }

    private static readonly Guid Row = Guid.Parse("3F2A1B4C-5D6E-4F70-8A91-B2C3D4E5F642");

    /// <summary>
    /// The warehouse run: the flat's groceries, exempt, and a jacket that is nobody's
    /// business but yours, which is not.
    /// </summary>
    private static BankTransactionSeedDto Warehouse(decimal charge = 158.07m, bool billed = true) => new()
    {
        Id = Row,
        DaysAgo = 2,
        Amount = charge,
        Description = "COSTCO WHOLESALE 718",
        MerchantName = "Costco",
        Receipt = billed ? Bill() : null
    };

    private static ReceiptSeedDto Bill() => new()
    {
        Tax = 19.55m,
        Items =
        [
            new ReceiptItemSeedDto
            {
                Name = "Rotisserie chicken", Price = 53.54m, Taxable = false, Shared = true
            },
            new ReceiptItemSeedDto
            {
                Name = "Fleece jacket", Price = 34.99m,
                Had = new Dictionary<Guid, int> { [Guid.Parse(Anabel)] = 1 }
            },
            new ReceiptItemSeedDto
            {
                Name = "Running shoes", Price = 49.99m,
                Had = new Dictionary<Guid, int> { [Guid.Parse(Anabel)] = 1 }
            }
        ]
    };

    private static BankConnectionSeedDto Connection(BankTransactionSeedDto row) => new()
    {
        Id = Guid.Parse("3F2A1B4C-5D6E-4F70-8A91-B2C3D4E5F630"),
        UserId = Guid.Parse(Anabel),
        InstitutionName = "Tattersall Federal",
        Accounts =
        [
            new LinkedAccountSeedDto
            {
                Id = Guid.Parse("3F2A1B4C-5D6E-4F70-8A91-B2C3D4E5F631"),
                Name = "Joint Account",
                Type = "depository",
                Transactions = [row]
            }
        ]
    };

    /// <summary>
    /// A context nothing ever saves. What is under test is what gets added, so these read the
    /// change tracker rather than a database -- the provider is here only because
    /// <c>DbContext.Set&lt;T&gt;()</c> refuses to hand anything back without one.
    /// </summary>
    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IEnumerable<T> Tracked<T>(AppDbContext db) where T : class =>
        db.ChangeTracker.Entries<T>().Select(entry => entry.Entity);

    /// <summary>
    /// The step under test, on its own: the base class calls it only for a connection that
    /// is not seeded already, which is the whole reason the bills are added from here rather
    /// than while mapping.
    /// </summary>
    private static Task Add(AppDbContext db, BankConnectionSeedDto dto) =>
        new Probe(db, TimeProvider.System, new FakeSource<BankConnectionSeedDto>([dto]))
            .Add(dto);

    private sealed class Probe(
        AppDbContext db, TimeProvider clock, ISeedDataSource<BankConnectionSeedDto> source)
        : BankConnectionSeeder(db, clock, NullLogger<BankConnectionSeeder>.Instance, source)
    {
        public async Task Add(BankConnectionSeedDto dto)
        {
            // Mapped without touching the database: the real MapAsync looks the user up, and
            // this is not the step under test.
            var connection = new BankConnection
            {
                Id = dto.Id,
                UserId = dto.UserId,
                Provider = "seed",
                ProviderItemId = $"seed-{dto.Id:N}",
                InstitutionName = dto.InstitutionName,
                AccessTokenCiphertext = "seeded-connection-has-no-token",
                LinkedAt = DateTimeOffset.UnixEpoch
            };

            await AddEntityAsync(connection, dto);
        }
    }

    private sealed class FakeSource<T>(IReadOnlyList<T> items) : ISeedDataSource<T>
    {
        public async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
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
