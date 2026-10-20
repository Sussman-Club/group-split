using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

internal sealed class NativeTokenStore(ISecureStorage storage, ILogger<NativeTokenStore> logger) : INativeTokenStore
{
    internal const string Key = "groupsplit.native.tokens";

    public async Task<string?> ReadAsync()
    {
        try { return await storage.GetAsync(Key); }
        catch (Exception)
        {
            // Restored Android ciphertext may have lost its encryption key. Recover by
            // discarding only this app's session, never by falling back to plaintext.
            logger.LogWarning("Saved native credentials could not be read; the session will be cleared.");
            Clear();
            return null;
        }
    }

    public async Task WriteAsync(string value)
    {
        try { await storage.SetAsync(Key, value); }
        catch (Exception)
        {
            Clear();
            throw new InvalidOperationException("Credentials could not be saved securely. Try signing in without keeping the session.");
        }
    }

    public void Clear()
    {
        try { storage.Remove(Key); }
        catch (Exception)
        {
            logger.LogWarning("Secure storage could not remove the saved native session.");
        }
    }
}
