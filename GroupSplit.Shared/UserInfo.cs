using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

public record UserInfo(Guid Id, string? FirstName, string? LastName, string? Email)
{
    [JsonIgnore] public string FullName => $"{FirstName} {LastName}".Trim();
}