using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GroupSplit.API.Services.Banking;
using GroupSplit.Data;
using Microsoft.AspNetCore.DataProtection;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Where the Data Protection key ring lives, and what stops it being useful to somebody who
/// has the database.
/// </summary>
/// <remarks>
/// The ring is kept in the app database so that every instance reads what any other wrote,
/// and so that resetting the database takes the keys and the ciphertext they open together
/// rather than leaving one without the other.
/// <para>
/// On its own that would encrypt the bank access tokens against a leak of one table and
/// against nothing else: a database dump would carry the keys beside the ciphertext, which
/// is not a meaningful difference from storing the tokens in the clear. So the ring is
/// itself encrypted, with a certificate the deployment holds as a secret and the database
/// never sees. Data Protection keeps doing what it is good at -- rotating keys, reading
/// what older ones wrote -- and the thing that unlocks it lives somewhere else.
/// </para>
/// <para>
/// Locally there is usually no certificate, and the ring is left unwrapped. That is the
/// ordinary ASP.NET posture for development and it is stated out loud at startup, because
/// the failure this guards against is silence: a deployment that forgot the certificate and
/// looks exactly like one that has it.
/// </para>
/// </remarks>
public static class KeyRingExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddBankKeyRing()
        {
            var options = builder.Configuration.GetSection(BankingOptions.SectionName);

            var protection = builder.Services.AddDataProtection()
                .PersistKeysToDbContext<AppDbContext>()
                // Pinned, because it is part of key derivation: letting it default to the
                // content root path would orphan every stored token the day the app moved.
                .SetApplicationName("GroupSplit");

            var certificate = options[nameof(BankingOptions.KeyRingCertificate)];

            if (string.IsNullOrWhiteSpace(certificate))
                return builder;

            protection.ProtectKeysWithCertificate(Load(certificate));

            // Whether that certificate is the one the ring was actually wrapped with,
            // checked once at startup -- and only where there is a certificate to be wrong
            // about. Locally the ring is unwrapped, so there is no mismatch to find, and the
            // seeder stores a placeholder in place of a token that was never protected by
            // anything; a check here would refuse to start over ordinary development data.
            builder.Services.AddHostedService<BankKeyRingVerifier>();

            return builder;
        }
    }

    /// <summary>
    /// The certificate from a base64 PKCS#12, as a deployment secret carries it.
    /// </summary>
    /// <remarks>
    /// A string rather than a file, because the deployment already has a way to hand secrets
    /// to a container and does not have one for mounting files. Loaded with
    /// <see cref="X509KeyStorageFlags.EphemeralKeySet"/> so the private key stays in memory:
    /// the default writes it to a user key store, which a container does not really have and
    /// which would leave it on disk if it did.
    /// </remarks>
    internal static X509Certificate2 Load(string base64Pkcs12)
    {
        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(base64Pkcs12.Trim());
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException(
                $"{BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)} is not valid base64. "
                + "It should be a PKCS#12 certificate, base64 encoded.", e);
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, password: null,
                X509KeyStorageFlags.EphemeralKeySet);

            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    $"{BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)} has no private key, "
                    + "so it could wrap the key ring but never unwrap it.");
            }

            return certificate;
        }
        catch (CryptographicException e)
        {
            throw new InvalidOperationException(
                $"{BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)} could not be read as a "
                + "PKCS#12 certificate.", e);
        }
    }
}
