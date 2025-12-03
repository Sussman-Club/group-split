using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

public record CreateRuleRequest
{
    public Guid GroupId { get; init; }
    [Required(ErrorMessage = "Category is required")]
    [StringLength(64, ErrorMessage = "Category must be less than 64 characters.")]
    public string Category { get; init; } = null!;
    public RuleVersionDto Version { get; init; } = null!;
};

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PersonalRuleVersionDto), typeDiscriminator: "personal")]
[JsonDerivedType(typeof(PercentRuleVersionDto), typeDiscriminator: "percent")]
public abstract record RuleVersionDto;

public record PersonalRuleVersionDto : RuleVersionDto;

public record PercentRuleVersionDto : RuleVersionDto
{
    public Dictionary<Guid, decimal> Percentages { get; init; } = new();
}