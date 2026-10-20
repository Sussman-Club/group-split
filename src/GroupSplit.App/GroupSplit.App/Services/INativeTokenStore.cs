namespace GroupSplit.App.Services;

public interface INativeTokenStore
{
    Task<string?> ReadAsync();
    Task WriteAsync(string value);
    void Clear();
}
