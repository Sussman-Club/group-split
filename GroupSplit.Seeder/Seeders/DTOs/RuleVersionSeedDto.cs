using System.Text.Json.Serialization;

namespace GroupSplit.Seeder.Seeders.DTOs;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(PersonalRuleVersionSeedDto), typeDiscriminator: "Personal")]
[JsonDerivedType(typeof(PercentRuleVersionSeedDto), typeDiscriminator: "Percentage")]
public class RuleVersionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid RuleId { get; init; }
    public required DateOnly StartDate { get; init; }
}

public class PersonalRuleVersionSeedDto : RuleVersionSeedDto;

public class PercentRuleVersionSeedDto : RuleVersionSeedDto
{
    public required PercentRuleUserSeedDto[] Percentages { get; init; }
}

public class PercentRuleUserSeedDto
{
    public required Guid UserId { get; init; }
    public required decimal Percentage { get; init; }
}