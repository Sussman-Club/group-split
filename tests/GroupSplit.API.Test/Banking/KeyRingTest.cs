using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.Banking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// What protects the stored bank access tokens, and what happens when the thing that
/// protects them is wrong.
/// </summary>
/// <remarks>
/// The key ring lives in the app database, so on its own it would sit beside the ciphertext
/// it opens and a database dump would carry both. The certificate is what makes that not
/// true, which is why a deployment is refused without one and why a malformed one has to
/// fail loudly rather than quietly leaving the ring unwrapped.
/// </remarks>
public class KeyRingTest
{
    [Fact]
    public void A_token_protected_by_the_key_ring_comes_back_as_it_went_in()
    {
        const string token = "access-sandbox-de3ce8ef";

        var protector = Protector();

        Assert.Equal(token, protector.Unprotect(protector.Protect(token)));
    }

    [Fact]
    public void What_is_stored_does_not_contain_the_token()
    {
        const string token = "access-sandbox-de3ce8ef";

        var stored = Protector().Protect(token);

        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_somebody_edited_will_not_open()
    {
        var protector = Protector();
        var stored = protector.Protect("access-sandbox-de3ce8ef").ToCharArray();

        stored[^2] = stored[^2] == 'A' ? 'B' : 'A';

        Assert.Throws<AccessTokenUnreadableException>(() => protector.Unprotect(new string(stored)));
    }

    [Fact]
    public void Another_deployments_key_ring_cannot_read_it()
    {
        var stored = Protector().Protect("access-sandbox-de3ce8ef");

        // A failure a person can be told about and act on, rather than a raw crypto
        // exception surfacing as a 500 from a background sweep.
        Assert.Throws<AccessTokenUnreadableException>(() => Protector().Unprotect(stored));
    }

    [Fact]
    public void A_real_certificate_is_accepted_and_keeps_its_private_key()
    {
        var certificate = KeyRingExtensions.Load(Base64Certificate());

        // Without the private key it could wrap the ring and never unwrap it, which would
        // look fine until the first restart.
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void A_certificate_with_no_private_key_is_refused()
    {
        using var full = SelfSigned();
        var publicOnly = Convert.ToBase64String(
            X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert))
                .Export(X509ContentType.Pkcs12));

        var e = Assert.Throws<InvalidOperationException>(() => KeyRingExtensions.Load(publicOnly));

        Assert.Contains("private key", e.Message);
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("dGhpcyBpcyBub3QgYSBjZXJ0aWZpY2F0ZQ==")]
    public void Something_that_is_not_a_certificate_fails_loudly(string configured)
    {
        // Loudly, because the alternative is a deployment that silently leaves the key ring
        // unwrapped and looks exactly like one that did not.
        Assert.Throws<InvalidOperationException>(() => KeyRingExtensions.Load(configured));
    }

    private static IAccessTokenProtector Protector()
    {
        var services = new ServiceCollection();

        services.AddDataProtection().UseEphemeralDataProtectionProvider();

        return new DataProtectionAccessTokenProtector(
            services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    private static string Base64Certificate() =>
        Convert.ToBase64String(SelfSigned().Export(X509ContentType.Pkcs12));

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=GroupSplit Key Ring", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
    }
}
