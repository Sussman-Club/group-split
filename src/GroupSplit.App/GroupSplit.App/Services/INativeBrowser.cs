namespace GroupSplit.App.Services;

public interface INativeBrowser
{
    Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callback, CancellationToken ct);
}
