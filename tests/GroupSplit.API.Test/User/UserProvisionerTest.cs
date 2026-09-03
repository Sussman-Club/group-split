using System.Security.Claims;
using GroupSplit.API.Services;
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
        // The row is keyed on the subject claim, not the address, so two people sharing
        // an email -- or one recreated in Keycloak -- must not collapse into one user.
        var first = await Provision(Principal(Guid.NewGuid().ToString(), email: "shared@example.com"));
        var second = await Provision(Principal(Guid.NewGuid().ToString(), email: "shared@example.com"));

        Assert.NotEqual(first.Id, second.Id);
    }
}
