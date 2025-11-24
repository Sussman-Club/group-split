namespace GroupSplit.Seeder.Seeders.Base;

public interface IDatabaseSeeder
{
    int Order { get; }
    Task SeedAsync(CancellationToken ct = default);
}