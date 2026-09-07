using GroupSplit.Cli.Auth;

namespace GroupSplit.Cli.Test;

[Collection(EnvironmentCollection.Name)]
public sealed class TokenStoreTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly TokenStore _store = new();

    private static readonly Uri Authority = new("https://example.com/idp/realms/group-split");

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void Round_trips_a_credential()
    {
        var credential = new StoredCredential
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Username = "anabel"
        };

        _store.Save(Authority, "cli", credential);

        var loaded = _store.Get(Authority, "cli");

        Assert.Equal("access", loaded!.AccessToken);
        Assert.Equal("refresh", loaded.RefreshToken);
        Assert.Equal("anabel", loaded.Username);
    }

    [Fact]
    public void Credentials_for_different_servers_do_not_collide()
    {
        var other = new Uri("https://staging.example.com/idp/realms/group-split");

        _store.Save(Authority, "cli", new StoredCredential { AccessToken = "prod" });
        _store.Save(other, "cli", new StoredCredential { AccessToken = "staging" });

        Assert.Equal("prod", _store.Get(Authority, "cli")!.AccessToken);
        Assert.Equal("staging", _store.Get(other, "cli")!.AccessToken);
    }

    [Fact]
    public void Remove_reports_whether_there_was_anything_to_remove()
    {
        _store.Save(Authority, "cli", new StoredCredential { AccessToken = "a" });

        Assert.True(_store.Remove(Authority, "cli"));
        Assert.False(_store.Remove(Authority, "cli"));
        Assert.Null(_store.Get(Authority, "cli"));
    }

    [Fact]
    public void A_credential_within_a_minute_of_expiry_is_already_expired()
    {
        // The slack exists so a token cannot pass this check and then be rejected by the
        // API a few hundred milliseconds later, mid-request.
        Assert.True(new StoredCredential { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) }.IsExpired);
        Assert.False(new StoredCredential { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }.IsExpired);
    }

    [Fact]
    public void A_corrupt_credential_file_is_treated_as_no_credentials()
    {
        _store.Save(Authority, "cli", new StoredCredential { AccessToken = "a" });
        File.WriteAllText(_store.Path, "{ this is not json");

        Assert.Null(_store.Get(Authority, "cli"));
    }

    [Fact]
    public void The_credential_file_is_not_readable_by_other_users()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _store.Save(Authority, "cli", new StoredCredential { AccessToken = "a" });

        var mode = File.GetUnixFileMode(_store.Path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
