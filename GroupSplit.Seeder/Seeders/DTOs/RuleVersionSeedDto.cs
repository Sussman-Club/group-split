using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.Seeder.Seeders.DTOs;

public enum RuleVersionType
{
    Personal,
    Percentage,
}
public class RuleVersionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid RuleId { get; init; }
    public required DateOnly StartDate { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required RuleVersionType Type { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
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