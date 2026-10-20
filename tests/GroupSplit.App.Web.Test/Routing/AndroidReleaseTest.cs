using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Routing;

public sealed class AndroidReleaseTest
{
    [Theory]
    [InlineData(false, false, "android-v1.2.3-42", "GroupSplit.apk", true, true)]
    [InlineData(true, false, "android-v1.2.3-42", "GroupSplit.apk", true, false)]
    [InlineData(false, true, "android-v1.2.3-42", "GroupSplit.apk", true, false)]
    [InlineData(false, false, "cli-v1.2.3", "GroupSplit.apk", true, false)]
    [InlineData(false, false, "app-v1.2.3", "GroupSplit.apk", true, false)]
    [InlineData(false, false, "app-v1.2.3-42", "GroupSplit.apk", true, false)]
    [InlineData(false, false, "android-v1.2.3-42", "unsigned.apk", true, false)]
    [InlineData(false, false, "android-v1.2.3-42", "GroupSplit.apk", false, false)]
    public async Task Only_a_complete_published_android_release_is_offered(bool draft, bool prerelease, string tag, string apkName, bool checksum, bool available)
    {
        var transport = new Transport { Body = Release(draft, prerelease, tag, apkName, checksum) };
        using var catalog = new AndroidReleaseCatalog(new Clients(transport), TimeProvider.System);
        var release = await catalog.LatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(available, release is not null);
        if (available)
        {
            Assert.Equal("1.2.3-42", release!.Version);
            Assert.EndsWith("/android-v1.2.3-42/GroupSplit.apk", release.DownloadUrl);
        }
    }

    [Fact]
    public async Task Cached_release_survives_transient_outages_but_not_an_explicit_removal()
    {
        var transport = new Transport { Body = Release() };
        var clock = new Clock();
        using var catalog = new AndroidReleaseCatalog(new Clients(transport), clock);
        var release = await catalog.LatestAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(release);
        await catalog.LatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.Calls);
        clock.Now = clock.Now.AddMinutes(6);
        transport.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(release, await catalog.LatestAsync(TestContext.Current.CancellationToken));
        clock.Now = clock.Now.AddMinutes(1);
        transport.Status = HttpStatusCode.NotFound;
        Assert.Null(await catalog.LatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task External_asset_urls_are_never_advertised()
    {
        var transport = new Transport { Body = Release().Replace("https://github.com/", "https://attacker.example/") };
        using var catalog = new AndroidReleaseCatalog(new Clients(transport), TimeProvider.System);
        Assert.Null(await catalog.LatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Website_association_and_release_lookup_are_public_JSON_endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHttpClientFactory>(new Clients(new Transport { Body = Release() }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AndroidReleaseCatalog>();
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapAndroidDownloads();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var association = await client.GetAsync("/.well-known/assetlinks.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, association.StatusCode);
        Assert.Equal("application/json", association.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await association.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("club.sussman.groupsplit", json.RootElement[0].GetProperty("target").GetProperty("package_name").GetString());
        var release = await client.GetFromJsonAsync<AndroidReleaseInfo>("/downloads/android/latest", TestContext.Current.CancellationToken);
        Assert.NotNull(release);
    }

    private static string Release(bool draft = false, bool prerelease = false, string tag = "android-v1.2.3-42", string apkName = "GroupSplit.apk", bool checksum = true) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease,
            assets = new[]
            {
                new { name = apkName, size = 42000000, browser_download_url = $"https://github.com/Sussman-Club/group-split/releases/download/{tag}/{apkName}" },
                new { name = checksum ? "SHA256SUMS" : "other", size = 80, browser_download_url = $"https://github.com/Sussman-Club/group-split/releases/download/{tag}/SHA256SUMS" }
            }
        });

    private sealed class Clients(Transport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, false);
    }
    private sealed class Transport : HttpMessageHandler
    {
        public string Body { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
