using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GroupSplit.App.Shared.Services;

namespace GroupSplit.App.Web.Services;

/// <summary>Cache the public release lookup so a download page does not exhaust GitHub's anonymous quota.</summary>
public sealed class AndroidReleaseCatalog(IHttpClientFactory clients, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset expires;
    private AndroidReleaseInfo? cached;
    private const string Repository = "https://github.com/Sussman-Club/group-split/releases/download/";

    public async Task<AndroidReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() < expires) return cached;
            using var client = clients.CreateClient(nameof(AndroidReleaseCatalog));
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GroupSplit-Android-Downloads/1.0");
            try
            {
                using var response = await client.GetAsync("https://api.github.com/repos/Sussman-Club/group-split/releases/latest", ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    cached = null;
                    expires = clock.GetUtcNow().AddMinutes(5);
                    return null;
                }
                response.EnsureSuccessStatusCode();
                var release = await response.Content.ReadFromJsonAsync<Release>(ct);
                cached = Parse(release);
                expires = clock.GetUtcNow().AddMinutes(5);
            }
            catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Keep the last verified download during a temporary GitHub outage.
                expires = clock.GetUtcNow().AddSeconds(30);
            }
            return cached;
        }
        finally { gate.Release(); }
    }

    private static AndroidReleaseInfo? Parse(Release? release)
    {
        if (release is null || release.Draft || release.Prerelease || release.Tag?.StartsWith("app-v", StringComparison.Ordinal) != true) return null;
        var prefix = Repository + Uri.EscapeDataString(release.Tag) + "/";
        var apk = release.Assets?.FirstOrDefault(asset => asset.Name == "GroupSplit.apk" && asset.Url == prefix + "GroupSplit.apk" && asset.Size > 0);
        var checksum = release.Assets?.FirstOrDefault(asset => asset.Name == "SHA256SUMS" && asset.Url == prefix + "SHA256SUMS");
        return apk is null || checksum is null ? null : new(release.Tag[5..], apk.Url, checksum.Url, apk.Size);
    }

    public void Dispose() => gate.Dispose();

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] Asset[]? Assets);
    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size);
}
