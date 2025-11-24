namespace GroupSplit.Seeder.Seeders.Base;

public interface IDatabaseSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}