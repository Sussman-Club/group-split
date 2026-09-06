using System.Text.Json;

namespace GroupSplit.Cli.Api;

/// <summary>
/// The settings the generated client reads and writes with. Named and shaped to match
/// <c>GroupSplit.App.Shared.Models.GroupSplitSerializer</c> because the NSwag templates
/// name this method in <c>JsonSerializerSettingsTransformationMethod</c>; the two clients
/// have to agree on the wire format because they talk to the same API.
/// </summary>
public static class GroupSplitSerializer
{
    public static readonly JsonSerializerOptions Options = Transform(new JsonSerializerOptions());

    public static JsonSerializerOptions Transform(JsonSerializerOptions options)
    {
        return new JsonSerializerOptions(options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
