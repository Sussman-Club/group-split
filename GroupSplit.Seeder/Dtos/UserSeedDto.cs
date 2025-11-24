namespace GroupSplit.Seeder.Dtos;

public class UserSeedDto
{
    public required string ExternalUserId { get; set; }
    public string Name { get; set; } = "";
    public Guid? PersonalGroupId { get; set; }
    public List<Guid> GroupIds { get; set; } = [];
}