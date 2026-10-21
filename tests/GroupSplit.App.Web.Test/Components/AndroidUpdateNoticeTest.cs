using System.Net;
using System.Net.Http.Json;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Components;

public sealed class AndroidUpdateNoticeTest : ComponentTest
{
    private static readonly Uri Website = new("https://groupsplit.sussman.win/");

    [Fact]
    public void Newer_build_offers_public_download_page_and_can_be_dismissed()
    {
        var transport = Setup(41, "1.0.0-42");
        var notice = Render<AndroidUpdateNotice>();
        notice.WaitForAssertion(() => Assert.Contains("Update available", notice.Markup));
        var action = notice.Find("a");
        Assert.Equal("Update app", action.TextContent.Trim());
        Assert.Equal("https://groupsplit.sussman.win/android", action.GetAttribute("href"));
        Assert.Equal("_blank", action.GetAttribute("target"));
        Assert.Equal(new Uri(Website, "downloads/android/latest"), transport.Request!.RequestUri);
        Assert.Null(transport.Request.Headers.Authorization);
        notice.Find("button[aria-label='Dismiss update notice']").Click();
        Assert.Empty(notice.Markup);
        notice.Render();
        Assert.Empty(notice.Markup);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("1.0.0-41")]
    [InlineData("1.0.0-40")]
    [InlineData("2.0.0-40")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-invalid")]
    [InlineData(null)]
    public void Current_older_missing_or_invalid_releases_do_not_offer_an_update(string? version)
    {
        var transport = Setup(41, version);
        var notice = Render<AndroidUpdateNotice>();
        Assert.Equal(1, transport.Calls);
        Assert.Empty(notice.Markup);
    }

    [Theory]
    [InlineData(false, 41)]
    [InlineData(true, null)]
    [InlineData(true, 0)]
    public void Browser_or_unversioned_build_does_not_check_for_updates(bool native, int? build)
    {
        var transport = Setup(build, "1.0.0-42", native);
        var notice = Render<AndroidUpdateNotice>();
        Assert.Empty(notice.Markup);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("timeout")]
    [InlineData("json")]
    public void Failed_check_does_not_interrupt_the_app(string failure)
    {
        var transport = Setup(41, "1.0.0-42");
        transport.Failure = failure;
        var notice = Render<AndroidUpdateNotice>();
        Assert.Equal(1, transport.Calls);
        Assert.Empty(notice.Markup);
    }

    private Transport Setup(int? build, string? version, bool native = true)
    {
        Services.AddSingleton(new AppLinks(native ? Website : null) { AndroidBuild = build });
        var transport = new Transport(version);
        Services.AddSingleton<IHttpClientFactory>(new Clients(transport));
        return transport;
    }

    private sealed class Clients(Transport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, false);
    }

    private sealed class Transport(string? version) : HttpMessageHandler
    {
        public string? Failure { get; set; }
        public int Calls { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Request = request;
            if (Failure == "offline") throw new HttpRequestException();
            if (Failure == "timeout") throw new TaskCanceledException();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Failure == "json" ? new StringContent("invalid") : JsonContent.Create(
                    version is null ? null : new AndroidReleaseInfo(version,
                        "https://github.com/Sussman-Club/group-split/releases/download/android-v1.0.0-42/GroupSplit.apk",
                        "https://github.com/Sussman-Club/group-split/releases/download/android-v1.0.0-42/SHA256SUMS", 42))
            });
        }
    }
}
