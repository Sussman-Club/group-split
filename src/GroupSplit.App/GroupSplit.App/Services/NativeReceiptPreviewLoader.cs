using GroupSplit.App.Shared.Services;

namespace GroupSplit.App.Services;

internal sealed class NativeReceiptPreviewLoader(IHttpClientFactory clients) : IReceiptPreviewLoader
{
    public async Task<string> LoadAsync(string url, string contentType, CancellationToken cancellationToken = default)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : url;
        path = path.TrimStart('/');
        if (!path.StartsWith("api/", StringComparison.Ordinal))
        {
            throw new HttpRequestException("Invalid receipt preview path.");
        }
        using var client = clients.CreateClient("native-receipt-preview");
        var bytes = await client.GetByteArrayAsync(path[4..], cancellationToken);
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }
}
