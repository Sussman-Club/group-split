using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

internal sealed class NativeTokenStore(ISecureStorage storage, IPreferences preferences,
    ILogger<NativeTokenStore> logger) : INativeTokenStore
{
    internal const string Key = "groupsplit.native.tokens";
    internal const string DisabledKey = "groupsplit.native.session-disabled";

    public async Task<string?> ReadAsync()
    {
        try
        {
            if (preferences.Get(DisabledKey, false)) return null;
            return await storage.GetAsync(Key);
        }
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
        try
        {
            // Keep old credentials disabled until their replacement is securely saved.
            preferences.Set(DisabledKey, true);
            await storage.SetAsync(Key, value);
            preferences.Set(DisabledKey, false);
        }
        catch (Exception)
        {
            Clear();
            throw new InvalidOperationException("Credentials could not be saved securely. Try signing in without keeping the session.");
        }
    }

    public void Clear()
    {
        // This non-secret marker survives a restart even if SecureStorage cannot delete
        // its ciphertext. Never rely on clearing only the in-memory token session.
        var disabled = false;
        try { preferences.Set(DisabledKey, true); disabled = true; }
        catch (Exception) { logger.LogWarning("Saved native credentials could not be marked disabled."); }
        try { storage.Remove(Key); }
        catch (Exception)
        {
            if (!disabled) throw new NativeCredentialCleanupException();
            logger.LogWarning("Secure storage could not remove the saved native session.");
        }
    }
}

internal sealed class NativeCredentialCleanupException() : InvalidOperationException(
    "Saved sign-in could not be removed or disabled.");
