using System.Security.Claims;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared.Errors;
using GroupSplit.API.Test.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.User;

/// <summary>
/// Tests that the provisioner keeps the stored profile in step with the token.
/// Keycloak owns first name, last name and email -- the app only reads them -- so an
/// edit made in Keycloak has to reach the stored row on the next request.
/// </summary>
public class UserProvisionerTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static ClaimsPrincipal Principal(
        string subject,
        string? firstName = null,
        string? lastName = null,
        string? email = null)
    {
        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, subject)];

        if (firstName is not null) claims.Add(new Claim(ClaimTypes.GivenName, firstName));
        if (lastName is not null) claims.Add(new Claim(ClaimTypes.Surname, lastName));
        if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private Task<Data.Entities.User> Provision(ClaimsPrincipal principal) =>
        GetService<IUserProvisioner>().GetOrCreate(principal, TestContext.Current.CancellationToken);

    private Task<Data.Entities.User?> Stored(string subject) =>
        DbContext.Set<Data.Entities.User>()
            .FirstOrDefaultAsync(
                user => user.Identity.IdentityId == subject,
                TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_profile_edited_in_keycloak_reaches_the_stored_user()
    {
        var subject = Guid.NewGuid().ToString();
        await Provision(Principal(subject, "Ada", "Lovelace", "ada@example.com"));

        await Provision(Principal(subject, "Ada", "Byron", "ada.byron@example.com"));

        var stored = await Stored(subject);

        Assert.NotNull(stored);
        Assert.Equal("Ada", stored.FirstName);
        Assert.Equal("Byron", stored.LastName);
        Assert.Equal("ada.byron@example.com", stored.Email);
    }

    [Fact]
    public async Task A_claim_cleared_in_keycloak_clears_the_stored_value()
    {
        var subject = Guid.NewGuid().ToString();
        await Provision(Principal(subject, "Ada", "Lovelace", "ada@example.com"));

        // An admin clearing a field means Keycloak stops sending the claim at all.
        await Provision(Principal(subject, email: "ada@example.com"));

        var stored = await Stored(subject);

        Assert.NotNull(stored);
        Assert.Null(stored.FirstName);
        Assert.Null(stored.LastName);
        Assert.Equal("ada@example.com", stored.Email);
    }

    [Fact]
    public async Task An_unchanged_profile_leaves_the_user_clean()
    {
        // The middleware provisions on every authenticated request, so the common case
        // -- nothing changed -- must not dirty the entity and force a write.
        var subject = Guid.NewGuid().ToString();
        var principal = Principal(subject, "Ada", "Lovelace", "ada@example.com");

        await Provision(principal);

        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IUserProvisioner>();

        await provisioner.GetOrCreate(principal, TestContext.Current.CancellationToken);

        Assert.All(
            context.ChangeTracker.Entries<Data.Entities.User>(),
            entry => Assert.Equal(EntityState.Unchanged, entry.State));
    }

    [Fact]
    public async Task A_different_subject_is_a_different_user()
    {
        // The row is keyed on the subject claim, not the address.
        var first = await Provision(Principal(Guid.NewGuid().ToString(), email: "first@example.com"));
        var second = await Provision(Principal(Guid.NewGuid().ToString(), email: "second@example.com"));

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// Somebody recreated in Keycloak: a new subject, and an address the old row holds. Not
    /// linked to that row and not given a second one -- the address is unique, and this test
    /// used to say the opposite and pass only because the in-memory provider ignores unique
    /// indexes. The Postgres version, races included, is <c>UserProvisioningTest</c> in
    /// GroupSplit.AppHost.Test.
    /// </summary>
    [Fact]
    public async Task A_new_subject_with_an_email_another_user_holds_is_refused_with_a_code()
    {
        var first = await Provision(Principal(Guid.NewGuid().ToString(), email: "shared@example.com"));
        var recreated = Guid.NewGuid().ToString();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Provision(Principal(recreated, email: "shared@example.com")));

        Assert.Equal(ErrorCodes.IdentityEmailInUse, refusal.Code);
        Assert.Null(await Stored(recreated));
        Assert.Equal(first.Id, Assert.Single(
            await DbContext.Set<Data.Entities.User>()
                .Where(user => user.Email == "shared@example.com")
                .ToListAsync(TestContext.Current.CancellationToken)).Id);
    }

    /// <summary>
    /// An address change onto one another row holds is not taken, and not refused either:
    /// the caller is known by their subject, so they carry on under the stored address while
    /// the rest of the profile still follows Keycloak.
    /// </summary>
    [Fact]
    public async Task An_email_another_user_holds_is_not_taken_and_the_rest_of_the_profile_still_is()
    {
        var subject = Guid.NewGuid().ToString();
        await Provision(Principal(Guid.NewGuid().ToString(), email: "taken@example.com"));
        await Provision(Principal(subject, "Ada", "Lovelace", "ada@example.com"));

        var user = await Provision(Principal(subject, "Ada", "Byron", "taken@example.com"));

        Assert.Equal("ada@example.com", user.Email);
        Assert.Equal("Byron", user.LastName);
    }
}
