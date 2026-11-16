namespace GroupSplit.Seeder.Seeders.DTOs;

public class GroupSeedDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Currency { get; set; } = "USD";
}
