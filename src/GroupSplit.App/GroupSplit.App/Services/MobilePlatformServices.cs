using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

internal sealed class MobileBrowser : IMobileBrowser
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callback, CancellationToken ct)
    {
        var result = await WebAuthenticator.Default.AuthenticateAsync(
            new WebAuthenticatorOptions { Url = url, CallbackUrl = callback }, ct);
        return new Dictionary<string, string>(result.Properties);
    }
}

internal sealed class MobileTokenStore : IMobileTokenStore
{
    private const string Key = "groupsplit.mobile.tokens";
    public Task<string?> ReadAsync() => SecureStorage.Default.GetAsync(Key);
    public Task WriteAsync(string value) => SecureStorage.Default.SetAsync(Key, value);
    public void Clear() => SecureStorage.Default.Remove(Key);
}

internal sealed class ClientOptionsSetter(MobileOptions options) : IClientOptionsSetter
{
    public void ConfigureClient(HttpClientFactoryOptions factory)
    {
        factory.HttpClientActions.Add(client => client.BaseAddress = options.ApiBaseAddress);
        factory.HttpMessageHandlerBuilderActions.Add(builder =>
            builder.AdditionalHandlers.Add(ActivatorUtilities.CreateInstance<AuthDelegatingHandler>(builder.Services)));
    }
}
