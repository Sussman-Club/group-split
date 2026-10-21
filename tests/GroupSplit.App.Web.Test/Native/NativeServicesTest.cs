using System.Security.Cryptography;
using System.Text.Json;
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
        var store = new NativeTokenStore(storage.Object, Preferences().Object, NullLogger<NativeTokenStore>.Instance);

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
        var store = new NativeTokenStore(storage.Object, Preferences().Object, NullLogger<NativeTokenStore>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("token-set"));
        Assert.Contains("could not be saved securely", error.Message);
        Assert.DoesNotContain("platform failure", error.Message);
        storage.Verify(value => value.Remove(NativeTokenStore.Key), Times.Once);
    }

    [Fact]
    public async Task Failed_removal_and_cancelled_browser_logout_cannot_restore_a_session_after_restart()
    {
        var saved = JsonSerializer.Serialize(new NativeSession.Tokens("old-access", "old-refresh", "id", DateTimeOffset.UtcNow.AddHours(1)));
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.GetAsync(NativeTokenStore.Key)).ReturnsAsync(saved);
        storage.Setup(value => value.Remove(NativeTokenStore.Key)).Throws<PlatformNotSupportedException>();
        var preferences = Preferences();
        var options = NativeOptions.Create("https://api.example/", "https://identity.example/realm", false);
        var browser = new Mock<INativeBrowser>();
        browser.Setup(value => value.AuthenticateAsync(It.IsAny<Uri>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        using (var session = new NativeSession(options, Mock.Of<IHttpClientFactory>(), browser.Object,
            new NativeTokenStore(storage.Object, preferences.Object, NullLogger<NativeTokenStore>.Instance)))
        {
            Assert.Equal("old-access", await session.AccessTokenAsync());
            await Assert.ThrowsAsync<OperationCanceledException>(() => session.LogoutAsync());
        }

        // Fresh instances have no memory of sign-out, and the old ciphertext still exists.
        using var restarted = new NativeSession(options, Mock.Of<IHttpClientFactory>(), browser.Object,
            new NativeTokenStore(storage.Object, preferences.Object, NullLogger<NativeTokenStore>.Instance));
        Assert.Null(await restarted.AccessTokenAsync());
        storage.Verify(value => value.GetAsync(NativeTokenStore.Key), Times.Once);
    }

    [Fact]
    public async Task A_new_remembered_session_replaces_the_disabled_credentials()
    {
        var saved = "old-session";
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.Remove(NativeTokenStore.Key)).Throws<PlatformNotSupportedException>();
        storage.Setup(value => value.SetAsync(NativeTokenStore.Key, It.IsAny<string>()))
            .Callback<string, string>((_, value) => saved = value).Returns(Task.CompletedTask);
        storage.Setup(value => value.GetAsync(NativeTokenStore.Key)).ReturnsAsync(() => saved);
        var preferences = Preferences();
        var store = new NativeTokenStore(storage.Object, preferences.Object, NullLogger<NativeTokenStore>.Instance);
        store.Clear();
        Assert.Null(await store.ReadAsync());

        await store.WriteAsync("new-session");

        var restarted = new NativeTokenStore(storage.Object, preferences.Object, NullLogger<NativeTokenStore>.Instance);
        Assert.Equal("new-session", await restarted.ReadAsync());
    }

    [Fact]
    public void Failure_of_both_storage_mechanisms_is_reported_instead_of_claiming_saved_sign_out()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(value => value.Remove(NativeTokenStore.Key)).Throws<PlatformNotSupportedException>();
        var preferences = new Mock<IPreferences>();
        preferences.Setup(value => value.Set(NativeTokenStore.DisabledKey, true, null)).Throws<PlatformNotSupportedException>();
        var store = new NativeTokenStore(storage.Object, preferences.Object, NullLogger<NativeTokenStore>.Instance);
        Assert.Throws<NativeCredentialCleanupException>(() => store.Clear());
    }

    private static Mock<IPreferences> Preferences()
    {
        var values = new Dictionary<string, bool>();
        var preferences = new Mock<IPreferences>();
        preferences.Setup(value => value.Get(It.IsAny<string>(), It.IsAny<bool>(), null))
            .Returns((string key, bool fallback, string? _) => values.GetValueOrDefault(key, fallback));
        preferences.Setup(value => value.Set(It.IsAny<string>(), It.IsAny<bool>(), null))
            .Callback((string key, bool value, string? _) => values[key] = value);
        return preferences;
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
