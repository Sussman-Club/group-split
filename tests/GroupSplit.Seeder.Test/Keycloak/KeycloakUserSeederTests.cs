using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Seeders;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GroupSplit.Seeder.Test.Keycloak;

/// <summary>
/// What the seeder decides for each demo account: leave it alone, replace it, or create it.
/// The realm is faked, because none of these decisions are about HTTP — they are about
/// keeping one id on both sides of the sign-in, which is the thing that makes a seeded
/// account usable at all.
/// </summary>
public class KeycloakUserSeederTests
{
    private const string SeededId = "9F8E7D6C-5B4A-3C2D-1E0F-1234567890AB";
    private const string SomeOtherId = "33653a72-5d59-4fa0-ada5-0a465864e0a9";
    private const string Email = "anabel@test.com";

    private static UserSeedDto Seed(string? email = Email, string? password = null) => new()
    {
        Id = Guid.NewGuid(),
        ExternalUserId = SeededId,
        FirstName = "Anabel",
        LastName = "Benítez",
        Email = email,
        Password = password
    };

    private static KeycloakUserSeeder SeederFor(
        FakeKeycloak keycloak,
        KeycloakSeedOptions options,
        params UserSeedDto[] users) =>
        new(keycloak,
            new FakeSource<UserSeedDto>(users),
            MsOptions.Create(options),
            NullLogger<KeycloakUserSeeder>.Instance);

    private static KeycloakSeedOptions Configured(bool replaceConflicting = true) => new()
    {
        AdminUser = "admin",
        AdminPassword = "secret",
        DefaultPassword = "GroupSplit123!",
        ReplaceConflictingUsers = replaceConflicting
    };

    [Fact]
    public async Task An_account_is_created_under_the_id_the_app_database_is_linked_to()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        var created = Assert.Single(keycloak.Created);
        Assert.Equal(SeededId, created.Id);
        Assert.Equal(Email, created.Email);
        // The realm registers with the email as the username, so there is one thing to type.
        Assert.Equal(Email, created.Username);
        Assert.Equal("Anabel", created.FirstName);
        Assert.Empty(keycloak.Deleted);
    }

    [Fact]
    public async Task A_created_account_can_be_signed_in_to_straight_away()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        var credential = Assert.Single(Assert.Single(keycloak.Created).Credentials);
        Assert.Equal("GroupSplit123!", credential.Value);
        Assert.False(credential.Temporary, "a demo account that demands a password change is a nuisance");
        Assert.True(Assert.Single(keycloak.Created).Enabled);
        Assert.True(Assert.Single(keycloak.Created).EmailVerified);
    }

    [Fact]
    public async Task A_seed_entry_may_name_its_own_password()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, Configured(), Seed(password: "SomethingElse1!"))
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("SomethingElse1!", Assert.Single(Assert.Single(keycloak.Created).Credentials).Value);
    }

    /// <summary>
    /// The realm keeps its users in a volume, so accounts the seeder created before it started
    /// granting the default role outlive the fix -- and the branch above would otherwise leave
    /// them exactly as they are. Without that role Keycloak's own account console answers 401
    /// to everything it asks for, and nothing else in the system would ever put it right.
    /// </summary>
    [Fact]
    public async Task An_account_seeded_without_the_default_role_has_it_granted_on_the_next_run()
    {
        var keycloak = new FakeKeycloak();
        keycloak.Add(SeededId, Email);

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        Assert.Contains(SeededId, keycloak.WithDefaultRole);
        // Repaired in place: nothing about the account itself is worth recreating.
        Assert.Empty(keycloak.Created);
        Assert.Empty(keycloak.Deleted);
    }

    /// <summary>A created account already carries it, so a rerun has nothing to repair.</summary>
    [Fact]
    public async Task An_account_that_already_holds_the_default_role_is_not_granted_it_again()
    {
        var keycloak = new FakeKeycloak();
        keycloak.Add(SeededId, Email);
        keycloak.WithDefaultRole.Add(SeededId);

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        Assert.Single(keycloak.Calls, call => call == $"EnsureDefaultRole:{SeededId}");
        Assert.Empty(keycloak.Created);
    }

    /// <summary>Reruns are ordinary: the seeder runs on every start of the resource.</summary>
    [Fact]
    public async Task An_account_already_under_the_seeded_id_is_left_alone()
    {
        var keycloak = new FakeKeycloak();
        keycloak.Add(SeededId, Email);

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(keycloak.Created);
        Assert.Empty(keycloak.Deleted);
    }

    /// <summary>
    /// The case the whole seeder exists for: someone registered the demo address by hand, so
    /// the realm holds it under a subject the seeded database row has never heard of. Left
    /// alone, every request from that account collides on the unique email index.
    /// </summary>
    [Fact]
    public async Task An_account_holding_a_seeded_email_under_another_id_is_replaced()
    {
        var keycloak = new FakeKeycloak();
        keycloak.Add(SomeOtherId, Email);

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SomeOtherId, Assert.Single(keycloak.Deleted));
        Assert.Equal(SeededId, Assert.Single(keycloak.Created).Id);
    }

    [Fact]
    public async Task The_replacement_can_be_turned_off_and_then_nothing_is_touched()
    {
        var keycloak = new FakeKeycloak();
        keycloak.Add(SomeOtherId, Email);

        await SeederFor(keycloak, Configured(replaceConflicting: false), Seed())
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(keycloak.Deleted);
        Assert.Empty(keycloak.Created);
    }

    /// <summary>
    /// The seeder runs wherever the worker does, and only the AppHost supplies credentials.
    /// Without them the database half still seeds and this half says why it did not.
    /// </summary>
    [Fact]
    public async Task Without_admin_credentials_nothing_is_attempted()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, new KeycloakSeedOptions(), Seed())
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.False(keycloak.SignedIn);
        Assert.Empty(keycloak.Created);
    }

    /// <summary>Credentials are checked once, before any account is touched.</summary>
    [Fact]
    public async Task The_admin_signs_in_before_any_account_is_read()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, Configured(), Seed()).SeedAsync(TestContext.Current.CancellationToken);

        Assert.True(keycloak.SignedIn);
        Assert.Equal("SignIn", keycloak.Calls[0]);
    }

    [Fact]
    public async Task A_seed_user_with_no_email_cannot_become_an_account_and_is_skipped()
    {
        var keycloak = new FakeKeycloak();

        await SeederFor(keycloak, Configured(), Seed(email: null))
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(keycloak.Created);
    }

    [Fact]
    public async Task Every_seed_user_is_considered_not_only_the_first()
    {
        var keycloak = new FakeKeycloak();

        var users = new[]
        {
            Seed(),
            new UserSeedDto
            {
                Id = Guid.NewGuid(), ExternalUserId = "0A1B2C3D-4E5F-6071-8293-0A1B2C3D4E5F",
                FirstName = "Omar", Email = "omar@test.com"
            }
        };

        await SeederFor(keycloak, Configured(), users).SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, keycloak.Created.Count);
    }

    // ---- Fakes -------------------------------------------------------------------------

    private sealed class FakeSource<T>(IReadOnlyList<T> items) : ISeedDataSource<T>
    {
        public async IAsyncEnumerable<T> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>A realm in a dictionary, keyed by id, plus a record of what was asked of it.</summary>
    private sealed class FakeKeycloak : IKeycloakAdminClient
    {
        private readonly Dictionary<string, KeycloakUserSummary> _users = new(StringComparer.Ordinal);

        public string Realm => "group-split";

        public bool SignedIn { get; private set; }

        public List<string> Calls { get; } = [];

        public List<KeycloakUser> Created { get; } = [];

        public List<string> Deleted { get; } = [];

        /// <summary>Ids the realm has granted its default role to.</summary>
        public HashSet<string> WithDefaultRole { get; } = new(StringComparer.Ordinal);

        public void Add(string id, string email) =>
            _users[id] = new KeycloakUserSummary { Id = id, Email = email, Username = email };

        public Task SignInAsync(CancellationToken ct = default)
        {
            SignedIn = true;
            Calls.Add("SignIn");
            return Task.CompletedTask;
        }

        public Task<KeycloakUserSummary?> FindByIdAsync(string id, CancellationToken ct = default)
        {
            Calls.Add($"FindById:{id}");
            return Task.FromResult(_users.GetValueOrDefault(id));
        }

        public Task<KeycloakUserSummary?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            Calls.Add($"FindByEmail:{email}");
            return Task.FromResult(
                _users.Values.FirstOrDefault(user =>
                    string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));
        }

        public Task CreateAsync(KeycloakUser user, CancellationToken ct = default)
        {
            Calls.Add($"Create:{user.Id}");
            Created.Add(user);
            Add(user.Id, user.Email);
            // As the real client does: a created account comes with the realm's default role.
            WithDefaultRole.Add(user.Id);
            return Task.CompletedTask;
        }

        public Task<bool> EnsureDefaultRoleAsync(string id, CancellationToken ct = default)
        {
            Calls.Add($"EnsureDefaultRole:{id}");
            return Task.FromResult(WithDefaultRole.Add(id));
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Calls.Add($"Delete:{id}");
            Deleted.Add(id);
            _users.Remove(id);
            return Task.CompletedTask;
        }
    }
}
