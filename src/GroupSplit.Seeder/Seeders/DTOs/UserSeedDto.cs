namespace GroupSplit.Seeder.Seeders.DTOs;

public class UserSeedDto
{
    public required Guid Id { get; set; }
    public required string ExternalUserId { get; set; }
    public required string FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public IReadOnlyCollection<Guid> GroupIds { get; set; } = [];
}