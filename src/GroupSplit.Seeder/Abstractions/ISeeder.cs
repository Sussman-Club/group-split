namespace GroupSplit.Seeder.Abstractions;

/// <summary>
///     Defines a seeding operation responsible for populating initial data.
/// </summary>
public interface ISeeder
{
    Task SeedAsync(CancellationToken ct = default);
}