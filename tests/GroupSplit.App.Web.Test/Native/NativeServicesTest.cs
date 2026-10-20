using System.Security.Cryptography;
using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Storage;
using Moq;

namespace GroupSplit.App.Web.Test.Native;

public sealed class NativeServicesTest
{
    [Fact]
    public async Task Unreadable_secure_credentials_are_removed_without_wiping_other_storage()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.GetAsync(NativeTokenStore.Key))
            .ThrowsAsync(new CryptographicException("Unreadable restored ciphertext"));
        var store = new NativeTokenStore(storage.Object, NullLogger<NativeTokenStore>.Instance);

        Assert.Null(await store.ReadAsync());
        storage.Verify(value => value.Remove(NativeTokenStore.Key), Times.Once);
        storage.Verify(value => value.RemoveAll(), Times.Never);
    }

    [Fact]
    public async Task A_secure_storage_write_failure_never_reports_a_persisted_session()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.SetAsync(NativeTokenStore.Key, "token-set"))
            .ThrowsAsync(new InvalidOperationException("platform failure"));
        var store = new NativeTokenStore(storage.Object, NullLogger<NativeTokenStore>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("token-set"));
        Assert.Contains("could not be saved securely", error.Message);
        Assert.DoesNotContain("platform failure", error.Message);
        storage.Verify(value => value.Remove(NativeTokenStore.Key), Times.Once);
    }

    [Fact]
    public void Unavailable_storage_does_not_prevent_local_sign_out()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.Remove(NativeTokenStore.Key)).Throws<PlatformNotSupportedException>();
        var store = new NativeTokenStore(storage.Object, NullLogger<NativeTokenStore>.Instance);
        store.Clear();
    }

    [Fact]
    public void Native_windows_share_the_token_session_but_keep_their_user_state_scoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNativeServices(NativeOptions.Create("https://api.example/", "https://identity.example/realm", false));
        services.AddSingleton(Mock.Of<INativeBrowser>());
        services.AddSingleton(Mock.Of<INativeTokenStore>());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<NativeSession>(), second.ServiceProvider.GetRequiredService<NativeSession>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IUserLogin>(), second.ServiceProvider.GetRequiredService<IUserLogin>());
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(NativeSession.IdentityClientName);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.Null(client.DefaultRequestHeaders.Authorization);

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NativeSession.IdentityClientName);
        while (handler is DelegatingHandler { InnerHandler: not null } delegating) handler = delegating.InnerHandler;
        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.UseCookies);
        Assert.False(primary.AllowAutoRedirect);
    }

    [Fact]
    public void Missing_build_metadata_has_a_configuration_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeOptions.FromAssembly(typeof(NativeServicesTest).Assembly, false));
        Assert.Contains("Configure the native API URL", error.Message);
    }
}
