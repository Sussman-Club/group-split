using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptPreviewTest : ComponentTest
{
    [Fact]
    public async Task Native_loader_uses_api_base_address_for_receipt_path()
    {
        var handler = new RecordingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("native-receipt-preview"))
            .Returns(() => new HttpClient(handler, false) { BaseAddress = new Uri("https://example.com/api/") });
        var loader = new NativeReceiptPreviewLoader(factory.Object);

        var result = await loader.LoadAsync("/api/receipts/inbox/123/files/456", "image/jpeg");

        Assert.Equal("https://example.com/api/receipts/inbox/123/files/456", handler.RequestUri?.ToString());
        Assert.Equal("data:image/jpeg;base64,AQID", result);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
    }

    [Fact]
    public void Native_photo_preview_uses_authenticated_loader()
    {
        var loader = new Mock<IReceiptPreviewLoader>();
        loader.Setup(x => x.LoadAsync("/api/receipts/inbox/123/files/456", "image/jpeg", It.IsAny<CancellationToken>()))
            .ReturnsAsync("data:image/jpeg;base64,AQID");
        Services.AddSingleton(new AppLinks(new Uri("https://example.com/")));
        Services.AddSingleton(loader.Object);

        var preview = Render<ReceiptPreview>(parameters => parameters
            .Add(x => x.Url, "/api/receipts/inbox/123/files/456")
            .Add(x => x.ContentType, "image/jpeg"));

        Assert.Equal("data:image/jpeg;base64,AQID", preview.Find("img").GetAttribute("src"));
        loader.VerifyAll();
    }
}
