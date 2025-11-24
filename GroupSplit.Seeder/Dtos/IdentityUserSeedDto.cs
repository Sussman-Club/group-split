namespace GroupSplit.Seeder.Dtos;

public class IdentityUserSeedDto
{
    public required string Id { get; set; }
    public string UserName => Email;
    public required string Email { get; set; }
    public required string Password { get; set; }
    public bool EmailConfirmed { get; set; } = false;

    // public string[]? Roles { get; set; }
}