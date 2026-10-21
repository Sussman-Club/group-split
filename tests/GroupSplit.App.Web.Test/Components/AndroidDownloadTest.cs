using System.Net;
using System.Net.Http.Json;
using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Components;

public sealed class AndroidDownloadTest : ComponentTest
{
    [Fact]
    public void Published_release_has_an_installable_download_and_instructions()
    {
        var transport = new Transport { Available = true };
        Services.AddSingleton<IHttpClientFactory>(new Clients(transport));
        var page = Render<AndroidDownload>();
        page.WaitForAssertion(() => Assert.Contains("Download for Android", page.Markup));
        Assert.Equal("https://github.com/Sussman-Club/group-split/releases/download/app-v1.0.0-42/GroupSplit.apk",
            page.FindAll("a").Single(link => link.TextContent.Contains("Download for Android")).GetAttribute("href"));
        Assert.Contains("Android 7.0 or newer", page.Markup);
        Assert.Contains("allow this browser to install apps", page.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_release_or_network_failure_offers_a_retry_without_a_broken_download(bool failure)
    {
        var transport = new Transport { Failure = failure };
        Services.AddSingleton<IHttpClientFactory>(new Clients(transport));
        var page = Render<AndroidDownload>();
        page.WaitForAssertion(() => Assert.Contains("not available right now", page.Markup));
        Assert.DoesNotContain("Download for Android", page.Markup);
        transport.Failure = false;
        transport.Available = true;
        page.Find("button").Click();
        page.WaitForAssertion(() => Assert.Contains("Download for Android", page.Markup));
    }

    [Fact]
    public void Native_app_does_not_fetch_download_metadata_from_its_local_webview_origin()
    {
        Services.AddSingleton(new AppLinks(new Uri("https://groupsplit.sussman.win/")));
        Services.AddSingleton<IHttpClientFactory>(new Clients(new Transport { Failure = true }));
        var page = Render<AndroidDownload>();
        Assert.Contains("already using the Android app", page.Markup);
        Assert.Contains("https://groupsplit.sussman.win/android", page.Markup);
    }

    private sealed class Clients(Transport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, false);
    }
    private sealed class Transport : HttpMessageHandler
    {
        public bool Available { get; set; }
        public bool Failure { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure) throw new HttpRequestException("internal request details");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Available ? new AndroidReleaseInfo("1.0.0-42",
                    "https://github.com/Sussman-Club/group-split/releases/download/app-v1.0.0-42/GroupSplit.apk",
                    "https://github.com/Sussman-Club/group-split/releases/download/app-v1.0.0-42/SHA256SUMS", 42000000) : null)
            });
        }
    }
}
