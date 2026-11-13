namespace GroupSplit.App.Shared.Services;

public interface IReceiptPreviewLoader
{
    Task<string> LoadAsync(string url, string contentType, CancellationToken cancellationToken = default);
}
