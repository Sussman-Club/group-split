using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public record AddMemberRequest(HashSet<UserIdentifier> UserIdentifiers);

public record UserIdentifier
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = null!;
}