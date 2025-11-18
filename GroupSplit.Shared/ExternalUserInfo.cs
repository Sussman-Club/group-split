using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public class ExternalUserInfo
{
    [Required]
    public string Username { get; set; } = null!;

    [Required]
    public string ProviderKey { get; set; } = null!;
}
