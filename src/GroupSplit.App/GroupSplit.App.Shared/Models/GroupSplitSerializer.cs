using System.Text.Json;

namespace GroupSplit.App.Shared.Models;

public class GroupSplitSerializer
{
    public static JsonSerializerOptions Transform(JsonSerializerOptions options)
    {
        return new JsonSerializerOptions(options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}