using System.Data.Common;
using System.Security.Claims;
using Aspire.Hosting.Testing;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// First sign-in and profile refresh, against the real database.
/// </summary>
/// <remarks>
/// <c>User.Email</c> is unique, and the unit suite's in-memory provider does not enforce
/// unique indexes -- which is how a test there once asserted that somebody recreated in
/// Keycloak got a second row with the same address, while production answered every one of
/// their requests with a 500. Everything here is about what that index does: the refusal
/// when a new sign-in brings an address another row holds, the same sign-in racing itself on
/// its first requests, and an address change that collides.
/// <para>
/// Each test uses addresses of its own and deletes the rows it made, so the seeded data is
/// left as it was found.
/// </para>
/// </remarks>
public class UserProvisioningTest(AppHostFixture appHost) : IAsyncLifetime
{
    private readonly AppHostFixture _appHost = appHost;

    private readonly List<string> _subjects = [];

    private ServiceProvider? _services;

    /// <summary>Disarmed unless a test arms it; see <see cref="BeforeUserWrite"/>.</summary>
    private BeforeUserWrite _beforeUserWrite = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _appHost.SeedAsync();

        var connectionString = await _appHost.Application.GetConnectionStringAsync("db", Ct);

        var services = new ServiceCollection();

        services.AddLogging();
        _beforeUserWrite = new BeforeUserWrite();

        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(
            options => options.UseNpgsql(connectionString).AddInterceptors(_beforeUserWrite));
        services.AddCurrentUser();

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is null)
            return;

        using (var scope = _services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var userIds = await context.Set<UserIdentity>()
                .Where(identity => _subjects.Contains(identity.IdentityId))
                .Select(identity => identity.User.Id)
                .ToListAsync();

            await context.Set<UserIdentity>()
                .Where(identity => _subjects.Contains(identity.IdentityId))
                .ExecuteDeleteAsync();

            await context.Set<User>()
                .Where(user => userIds.Contains(user.Id))
                .ExecuteDeleteAsync();
        }

        await _services.DisposeAsync();
    }

    private string NewSubject()
    {
        var subject = Guid.NewGuid().ToString();
        _subjects.Add(subject);
        return subject;
    }

    private static string NewEmail() => $"provisioning-{Guid.NewGuid():N}@test.com";

    private static ClaimsPrincipal Principal(string subject, string email, string? lastName = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.GivenName, "Ada")
        ];

        if (lastName is not null) claims.Add(new Claim(ClaimTypes.Surname, lastName));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    /// <summary>One request's worth of provisioning: its own scope and context, as the middleware has.</summary>
    private async Task<User> Provision(ClaimsPrincipal principal)
    {
        using var scope = _services!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IUserProvisioner>()
            .GetOrCreate(principal, Ct);
    }

    private async Task<List<(string? Subject, string? Email)>> RowsWith(string email)
    {
        using var scope = _services!.CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<User>()
            .Where(user => user.Email == email)
            .Select(user => new { Subject = user.Identity.IdentityId, user.Email })
            .ToListAsync(Ct);

        return rows.Select(row => ((string?)row.Subject, row.Email)).ToList();
    }

    private async Task<User?> StoredBySubject(string subject)
    {
        using var scope = _services!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<User>()
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Identity.IdentityId == subject, Ct);
    }

    // ---- A new sign-in with a known address -----------------------------------------------

    [Fact(Timeout = 180_000)]
    public async Task A_new_subject_with_an_email_another_user_holds_is_refused_on_every_request()
    {
        var email = NewEmail();
        var original = await Provision(Principal(NewSubject(), email));
        var recreated = NewSubject();

        // Twice: it used to fail differently each time only in the sense that it failed
        // forever. It should be the same refusal, not a 500, every time.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refusal = await Assert.ThrowsAsync<ConflictException>(
                () => Provision(Principal(recreated, email)));

            Assert.Equal(ErrorCodes.IdentityEmailInUse, refusal.Code);
            Assert.Equal(409, refusal.Status);
        }

        Assert.Null(await StoredBySubject(recreated));
        var only = Assert.Single(await RowsWith(email));
        Assert.Equal(original.Identity.IdentityId, only.Subject);
    }

    /// <summary>
    /// The holder appears between the provisioner's check and its insert, so it is the
    /// unique index that refuses. Still the refusal, not the 500 the old catch produced by
    /// assuming every unique violation was the same subject racing itself.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task A_holder_that_arrives_between_the_check_and_the_insert_is_still_a_refusal()
    {
        var email = NewEmail();
        var holder = NewSubject();
        var latecomer = NewSubject();

        _beforeUserWrite.Arm(() => Provision(Principal(holder, email)));

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Provision(Principal(latecomer, email)));

        Assert.True(_beforeUserWrite.Fired, "the holder was meant to be written before the latecomer's insert");
        Assert.Equal(ErrorCodes.IdentityEmailInUse, refusal.Code);
        Assert.Null(await StoredBySubject(latecomer));
        Assert.Equal(holder, Assert.Single(await RowsWith(email)).Subject);
    }

    // ---- The same sign-in racing itself ---------------------------------------------------

    /// <summary>
    /// A person's first page load sends several requests at once, and each provisions. The
    /// one that loses the insert hits a unique index -- the address one, as it happens,
    /// since the user row is written before its identity -- and has to read back the row
    /// the winner made, not mistake it for somebody else holding the address.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task The_same_subject_racing_its_first_requests_ends_up_as_one_user()
    {
        var email = NewEmail();
        var subject = NewSubject();

        _beforeUserWrite.Arm(() => Provision(Principal(subject, email)));

        var loser = await Provision(Principal(subject, email));

        Assert.True(_beforeUserWrite.Fired, "the winner was meant to be written before the loser's insert");
        var stored = await StoredBySubject(subject);
        Assert.NotNull(stored);
        Assert.Equal(stored.Id, loser.Id);
        Assert.Single(await RowsWith(email));
    }

    [Fact(Timeout = 180_000)]
    public async Task Concurrent_first_requests_from_one_subject_all_get_the_same_user()
    {
        var email = NewEmail();
        var subject = NewSubject();

        var users = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => Provision(Principal(subject, email)), Ct)));

        Assert.Single(users.Select(user => user.Id).Distinct());
        Assert.Single(await RowsWith(email));
    }

    // ---- An address change that collides -------------------------------------------------

    [Fact(Timeout = 180_000)]
    public async Task An_email_change_onto_another_users_email_keeps_the_stored_one_and_goes_on()
    {
        var (mine, theirs) = (NewEmail(), NewEmail());
        var subject = NewSubject();
        var other = NewSubject();

        await Provision(Principal(subject, mine, "Lovelace"));
        await Provision(Principal(other, theirs));

        var user = await Provision(Principal(subject, theirs, "Byron"));

        Assert.Equal(mine, user.Email);

        var stored = await StoredBySubject(subject);
        Assert.NotNull(stored);
        Assert.Equal(mine, stored.Email);
        Assert.Equal("Byron", stored.LastName);
        Assert.Equal(other, Assert.Single(await RowsWith(theirs)).Subject);
    }

    /// <summary>
    /// Two people swap addresses in Keycloak. Refusing the change would leave each waiting
    /// on the other for good; carrying on under the stored address leaves both signed in.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Two_users_swapping_emails_both_carry_on()
    {
        var (a, b) = (NewEmail(), NewEmail());
        var (first, second) = (NewSubject(), NewSubject());

        await Provision(Principal(first, a));
        await Provision(Principal(second, b));

        var firstAfter = await Provision(Principal(first, b));
        var secondAfter = await Provision(Principal(second, a));

        Assert.Equal(a, firstAfter.Email);
        Assert.Equal(b, secondAfter.Email);
    }

    /// <summary>
    /// The address is taken between the refresh's check and its write, so the unique index
    /// refuses the update. The request still gets its user, as the row stands.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task An_email_taken_between_the_check_and_the_update_still_answers_with_the_user()
    {
        var (mine, wanted) = (NewEmail(), NewEmail());
        var subject = NewSubject();
        var other = NewSubject();

        var original = await Provision(Principal(subject, mine));

        _beforeUserWrite.Arm(() => Provision(Principal(other, wanted)));

        var user = await Provision(Principal(subject, wanted));

        Assert.True(_beforeUserWrite.Fired, "the other user was meant to take the address before the update");
        Assert.Equal(original.Id, user.Id);
        Assert.Equal(mine, user.Email);
        Assert.Equal(other, Assert.Single(await RowsWith(wanted)).Subject);
    }

    /// <summary>
    /// Runs something once, just before the next command that writes a <c>User</c> row is
    /// sent: the window between the provisioner's check and its write, which is where every
    /// race in these tests happens. Disarmed before the action runs, since the action
    /// provisions too and would otherwise trigger itself.
    /// </summary>
    private sealed class BeforeUserWrite : DbCommandInterceptor
    {
        private Func<Task>? _action;

        public bool Fired { get; private set; }

        public void Arm(Func<Task> action)
        {
            _action = action;
            Fired = false;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunIfWrite(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunIfWrite(command);
            return result;
        }

        private async Task RunIfWrite(DbCommand command)
        {
            if (_action is not { } action
                || !(command.CommandText.Contains("INSERT INTO \"User\" (")
                     || command.CommandText.Contains("UPDATE \"User\" SET")))
            {
                return;
            }

            _action = null;
            await action();
            Fired = true;
        }
    }
}
