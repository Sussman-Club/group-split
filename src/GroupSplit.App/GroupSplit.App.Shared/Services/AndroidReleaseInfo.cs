using System.Text.Json.Serialization;

namespace GroupSplit.App.Shared.Services;

public sealed record AndroidReleaseInfo(string Version, string DownloadUrl, string ChecksumUrl, long Size);

[JsonSerializable(typeof(AndroidReleaseInfo))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AndroidDownloadJsonContext : JsonSerializerContext { }
