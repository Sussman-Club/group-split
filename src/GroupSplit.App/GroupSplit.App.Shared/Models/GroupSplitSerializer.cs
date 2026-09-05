using System.Text.Json;

namespace GroupSplit.App.Shared.Models;

public class GroupSplitSerializer
{
    /// <summary>
    /// The settings the generated client reads and writes with, for the few places outside
    /// it that read part of a response by hand: the extension members of a problem.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Transform(new JsonSerializerOptions());

    public static JsonSerializerOptions Transform(JsonSerializerOptions options)
    {
        return new JsonSerializerOptions(options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
