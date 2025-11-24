namespace GroupSplit.Seeder.Dtos;

public class UserSeedDto
{
    public required Guid Id { get; set; }
    public required string ExternalUserId { get; set; }
    public Guid? PersonalGroupId { get; set; }
    public IReadOnlyCollection<Guid> GroupIds { get; set; } = [];
}