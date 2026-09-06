using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>A split rule as it appears in a list: enough to pick one by.</summary>
public record SplitRuleResponse(Guid Id, Guid GroupId, string Name);

/// <summary>A split rule with the division it stands for.</summary>
public record SplitRuleDetailsResponse
{
    public Guid Id { get; init; }

    public Guid GroupId { get; init; }

    public string Name { get; init; } = null!;

    public SplitRuleDto Definition { get; init; } = null!;
}

public record CreateSplitRuleRequest
{
    public Guid GroupId { get; init; }

    [Required(ErrorMessage = "Name is required")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; init; } = null!;

    public SplitRuleDto Definition { get; init; } = null!;
}

public record UpdateSplitRuleRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(64, ErrorMessage = "Name must be less than 64 characters.")]
    public string Name { get; set; } = null!;

    public SplitRuleDto Definition { get; set; } = null!;
}
