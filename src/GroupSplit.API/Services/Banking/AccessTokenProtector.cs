using Microsoft.AspNetCore.DataProtection;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Turns a provider's access token into the ciphertext a <c>BankConnection</c> stores, and
/// back.
/// </summary>
/// <remarks>
/// An interface over one call so that the tests above the seam can see a token go in and
/// come out without caring how, and so that the one place the key ring is chosen is the
/// host, not this class.
/// </remarks>
public interface IAccessTokenProtector
{
    string Protect(string accessToken);

    string Unprotect(string ciphertext);
}

/// <summary>
/// ASP.NET Data Protection, under one purpose string. The key ring is wherever the host
/// put it: the app database in production, ephemeral in the tests.
/// </summary>
public sealed class DataProtectionAccessTokenProtector(IDataProtectionProvider provider) : IAccessTokenProtector
{
    /// <summary>
    /// Part of every key derivation, so a protector made for anything else cannot read
    /// these. Changing it orphans every stored token; do not.
    /// </summary>
    private const string Purpose = "GroupSplit.BankConnection.AccessToken";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string accessToken) => _protector.Protect(accessToken);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
