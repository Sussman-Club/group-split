using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Amazon.S3;
using Amazon.S3.Model;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.API.Test.Endpoints;

public class MerchantImagesEndpointTest : IAsyncLifetime
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aSb8AAAAASUVORK5CYII=");
    private readonly Dictionary<string, byte[]> _objects = new();
    private readonly Mock<IAmazonS3> _storage = new(MockBehavior.Strict);
    private ApiEndpointHost _host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _storage.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns((PutObjectRequest request, CancellationToken _) =>
            {
                using var target = new MemoryStream();
                request.InputStream.CopyTo(target);
                _objects[request.Key] = target.ToArray();
                Assert.Equal("image/png", request.ContentType);
                Assert.StartsWith("merchant-logos/", request.Key);
                return Task.FromResult(new PutObjectResponse());
            });
        _storage.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string key, CancellationToken _) => _objects.TryGetValue(key, out var bytes)
                ? Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes) })
                : Task.FromException<GetObjectResponse>(new AmazonS3Exception("Missing") { StatusCode = HttpStatusCode.NotFound }));
        _storage.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string key, CancellationToken _) => { _objects.Remove(key); return Task.FromResult(new DeleteObjectResponse()); });
        _host = await ApiEndpointHost.StartAsync(services => services.AddSingleton(_storage.Object));
        _host.Client.DefaultRequestHeaders.Add("X-Test-Realm-Access", "{\"roles\":[\"groupsplit-staff\"]}");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Editing_a_merchant_saves_name_and_image_and_serves_only_the_image_publicly()
    {
        var created = await _host.Client.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = "Old shop" }, Ct);
        var original = (await created.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
        using var form = Upload("Corner Market", Png, original.Id);
        var response = await _host.Client.PostAsync("/staff/merchant-images", form, Ct);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
        Assert.Equal("Corner Market", saved.Name);
        Assert.True(MerchantLogos.IsAllowed(saved.LogoUrl));
        var persisted = await _host.Client.GetFromJsonAsync<MerchantResponse>($"/staff/merchants/{original.Id}", Ct);
        Assert.Equal(saved.LogoUrl, persisted!.LogoUrl);
        Assert.Equal(Png, Assert.Single(_objects).Value);

        using var anonymous = _host.AnonymousClient();
        var image = await anonymous.GetAsync("/merchant-images/" + saved.LogoUrl!.Split('/').Last(), Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync(Ct));
        Assert.True(image.Headers.CacheControl!.Public);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/staff/merchants/{original.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_new_merchant_can_be_created_with_its_image_in_one_save()
    {
        using var form = Upload("New coffee shop", Png);
        var response = await _host.Client.PostAsync("/staff/merchant-images", form, Ct);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
        Assert.Equal("New coffee shop", saved.Name);
        Assert.StartsWith(MerchantLogos.UploadedPath, saved.LogoUrl);
        Assert.Single(_objects);
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("too-large")]
    [InlineData("dimensions")]
    [InlineData("truncated")]
    public async Task Invalid_or_mislabelled_images_are_refused_before_storage(string kind)
    {
        var bytes = Png.ToArray();
        if (kind == "svg") bytes = "<svg onload='alert(1)'/>"u8.ToArray();
        if (kind == "too-large") bytes = new byte[MerchantLogos.MaximumUploadBytes + 1];
        if (kind == "dimensions") BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 513);
        if (kind == "truncated") bytes = bytes[..30];
        using var form = Upload("Shop", bytes);
        var response = await _host.Client.PostAsync("/staff/merchant-images", form, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_objects);
        _storage.Verify(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_merchant_update_removes_the_new_image_and_keeps_the_previous_record()
    {
        var first = await _host.Client.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = "First shop" }, Ct);
        var merchant = (await first.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
        await _host.Client.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = "Other shop" }, Ct);
        using var form = Upload("Other shop", Png, merchant.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.PostAsync("/staff/merchant-images", form, Ct)).StatusCode);
        Assert.Empty(_objects);
        var unchanged = await _host.Client.GetFromJsonAsync<MerchantResponse>($"/staff/merchants/{merchant.Id}", Ct);
        Assert.Equal("First shop", unchanged!.Name);
        Assert.Null(unchanged.LogoUrl);
        _storage.Verify(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_public_images_return_not_found()
    {
        using var anonymous = _host.AnonymousClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/merchant-images/{Guid.NewGuid():N}.png", Ct)).StatusCode);
    }

    private static MultipartFormDataContent Upload(string name, byte[] bytes, Guid? id = null)
    {
        var form = new MultipartFormDataContent();
        if (id is not null) form.Add(new StringContent(id.Value.ToString()), "merchantId");
        form.Add(new StringContent(name), "name");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");
        return form;
    }
}
