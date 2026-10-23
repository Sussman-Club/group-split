using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using GroupSplit.API.Services.Push;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.API.Test.Push;

public sealed class FirebaseCredentialTest
{
    [Fact]
    public async Task Deployment_credential_loads_as_a_service_account_without_a_file()
    {
        using var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new
        {
            type = "service_account", project_id = "test-project", private_key = rsa.ExportPkcs8PrivateKeyPem(),
            client_email = "push@test-project.iam.gserviceaccount.com", token_uri = "https://oauth2.googleapis.com/token"
        });
        var credential = await FirebasePushSender.CredentialAsync(Config(Encode(json)), TestContext.Current.CancellationToken);
        Assert.IsType<ServiceAccountCredential>(credential.UnderlyingCredential);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("e30=")]
    public async Task Invalid_configuration_is_rejected_without_exposing_its_contents(string encoded)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FirebasePushSender.CredentialAsync(Config(encoded), TestContext.Current.CancellationToken));
        Assert.Equal("The Firebase service-account configuration is invalid.", error.Message);
    }

    [Fact]
    public async Task A_different_credential_type_cannot_be_loaded_from_deployment_configuration()
    {
        var encoded = Encode("{\"type\":\"authorized_user\",\"client_id\":\"test\",\"client_secret\":\"test-secret\",\"refresh_token\":\"test-refresh\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FirebasePushSender.CredentialAsync(Config(encoded), TestContext.Current.CancellationToken));
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static IConfiguration Config(string value) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Push:ServiceAccountBase64"] = value }).Build();
}
