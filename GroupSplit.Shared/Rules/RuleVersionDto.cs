using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PersonalRuleVersionDto), typeDiscriminator: "personal")]
[JsonDerivedType(typeof(PercentRuleVersionDto), typeDiscriminator: "percent")]
[JsonDerivedType(typeof(SharesRuleVersionDto), typeDiscriminator: "shares")]
public abstract record RuleVersionDto;