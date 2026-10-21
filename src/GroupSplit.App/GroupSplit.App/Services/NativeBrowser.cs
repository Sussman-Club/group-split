using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace GroupSplit.App.Services;

internal sealed class NativeBrowser(IWebAuthenticator authenticator) : INativeBrowser
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callback, CancellationToken ct)
    {
        var result = await MainThread.InvokeOnMainThreadAsync(() => authenticator.AuthenticateAsync(
            new WebAuthenticatorOptions { Url = url, CallbackUrl = callback }, ct));
        return new Dictionary<string, string>(result.Properties);
    }
}
