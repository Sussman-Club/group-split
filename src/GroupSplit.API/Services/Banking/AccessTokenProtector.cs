using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Turns a provider's access token into the ciphertext a <c>BankConnection</c> stores, and
/// back.
/// </summary>
/// <remarks>
/// An interface over two calls so the tests above the seam can watch a token go in and come
/// out without caring how, and so that where the keys come from is the host's decision
/// rather than this class's.
/// </remarks>
public interface IAccessTokenProtector
{
    string Protect(string accessToken);

    /// <exception cref="AccessTokenUnreadableException">
    /// The stored value cannot be read with the keys this deployment has.
    /// </exception>
    string Unprotect(string ciphertext);
}

/// <summary>
/// A stored token that will not open: the key ring is gone or unreadable, or the value was
/// tampered with.
/// </summary>
/// <remarks>
/// Its own type because it has a sensible answer, and that answer is not "crash". The
/// connection is marked as needing attention and the person links the bank again, which
/// mints a new token. Nothing else can recover it -- that is what encrypting it means.
/// </remarks>
public sealed class AccessTokenUnreadableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// ASP.NET Data Protection, under one purpose string.
/// </summary>
/// <remarks>
/// The framework's, deliberately, rather than a few lines of AES written here. It rotates
/// its keys on its own and keeps reading what older ones wrote, which is the part that is
/// tedious to get right by hand, and it is maintained by people who do this for a living.
/// <para>
/// Where the key ring lives and what protects it is the host's business, and the host is
/// where the real decision sits: a ring kept in the app database with nothing wrapping it
/// protects these tokens from a leak of one table and from nothing else, because whoever
/// holds a database dump then holds the keys beside the ciphertext. See
/// <c>KeyRingExtensions</c> for what this deployment does about that.
/// </para>
/// </remarks>
public sealed class DataProtectionAccessTokenProtector(IDataProtectionProvider provider) : IAccessTokenProtector
{
    /// <summary>
    /// Part of every key derivation, so a protector made for anything else cannot read
    /// these. Changing it orphans every stored token; do not.
    /// </summary>
    private const string Purpose = "GroupSplit.BankConnection.AccessToken";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return _protector.Protect(accessToken);
    }

    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // No key in the ring wrote this, or the row was edited. Which of the two cannot
            // be known from here, and the answer is the same either way.
            throw new AccessTokenUnreadableException(
                "The stored access token could not be read with this deployment's key ring.", e);
        }
    }
}
