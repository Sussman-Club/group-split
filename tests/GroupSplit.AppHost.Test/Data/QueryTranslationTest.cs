using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.AppHost.Test.Base;
using Aspire.Hosting.Testing;
using GroupSplit.Data;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// Every read the app makes, run against the real database.
/// </summary>
/// <remarks>
/// The unit suite runs these same services on EF's in-memory provider, which evaluates on
/// the client whatever it cannot translate rather than refusing. So a query Npgsql rejects
/// passes there and 500s in production -- which is exactly what
/// <c>GET /invitations</c> did: it ordered by a member of a projected record, and the
/// in-memory provider sorted the objects it had already built while Npgsql could not
/// translate the constructor call at all.
/// <para>
/// These assert nothing about the rows. Executing the query is the assertion: a query that
/// does not translate throws before it returns anything, and every one of these would have
/// been red.
/// </para>
/// </remarks>
public class QueryTranslationTest(AppHostFixture appHost) : IAsyncLifetime
{
    private readonly AppHostFixture _appHost = appHost;

    private ServiceProvider? _services;
    private IServiceScope? _scope;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Service<T>() where T : notnull => _scope!.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// The production service list over a context pointed at the real database, with one
    /// of the seeded users signed in. Registered through <see cref="DomainServiceExtensions"/>
    /// rather than by hand, so this cannot cover a different set of services than runs.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await _appHost.SeedAsync();

        var connectionString = await _appHost.Application.GetConnectionStringAsync("db", Ct);

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(
            options => options.UseNpgsql(connectionString));
        services.AddDomainServices();

        _services = services.BuildServiceProvider();
        _scope = _services.CreateScope();

        var context = Service<AppDbContext>();

        // Somebody who is actually in a group, so the queries have rows to walk rather than
        // short-circuiting on an empty scope.
        var user = await context.Set<User>()
            .Where(candidate => candidate.Email != null && candidate.Groups.Any())
            .OrderBy(candidate => candidate.Email)
            .FirstAsync(Ct);

        Service<ICurrentUserInitializer>().Initialize(user);
    }

    public async ValueTask DisposeAsync()
    {
        _scope?.Dispose();

        if (_services is not null)
            await _services.DisposeAsync();
    }

    private async Task<Guid> AGroupOfTheirs()
    {
        var groups = await Service<IGroupService>().GetAllGroups(Ct);

        return await groups.Select(group => group.Id).FirstAsync(Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_invitations_addressed_to_me_translate()
    {
        await Service<IInvitationService>().Mine(Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_groups_pending_invitations_translate()
    {
        await Service<IInvitationService>().ForGroup(await AGroupOfTheirs(), Ct);
    }

    /// <summary>
    /// Both join-link projections, and the write between them: the group's own view of its
    /// link, and the view somebody following one gets. Each is a constructor call reaching
    /// through two navigations, which is the shape that does not translate.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_join_link_projections_translate()
    {
        var links = Service<IJoinLinkService>();
        var group = await AGroupOfTheirs();

        await links.ForGroup(group, Ct);

        var made = await links.Create(group, Ct);

        await links.ForGroup(group, Ct);
        await links.Describe(made.Token, Ct);

        await links.Revoke(group, Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_cross_group_position_translates()
    {
        await Service<IGroupService>().GetPosition(Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_groups_activity_translates()
    {
        var activity = await Service<IGroupService>().GetGroupActivity(await AGroupOfTheirs(), Ct);

        // Through the endpoint's own ordering and projection, which is where the shape that
        // has to translate actually lives.
        await activity
            .ApplySort(new SortRequest(), GroupApi.ActivitySort)
            .SelectActivityDto()
            .ToPageAsync(new PageRequest(), Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_groups_own_expense_listing_translates()
    {
        var expenses = await Service<ITransactionService>().InGroup(await AGroupOfTheirs(), Ct);

        await expenses
            .ApplyFilter(new TransactionFilter())
            .ApplySort(new SortRequest(), TransactionApi.Sort)
            .ToPageAsync(new PageRequest(), Ct);
    }

    /// <summary>
    /// The inbox listing, through the endpoint's own sort and projection. It is the only
    /// read in the app that orders by a <c>DateOnly</c>, projects an enum through a nested
    /// conditional, and reaches two navigations deep for the account and its institution --
    /// each of which the in-memory provider would happily evaluate on the client.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_inbox_listing_translates()
    {
        var rows = await Service<IInboxService>().List(new InboxFilter(), Ct);

        await rows
            .ApplySort(new SortRequest(), InboxApi.Sort)
            .SelectDto()
            .ToPageAsync(new PageRequest(), Ct);
    }

    /// <summary>
    /// Every key the inbox offers, in both directions. A key that does not translate is a
    /// 500 the moment somebody clicks that column, and nothing else would catch it.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("date")]
    [InlineData("amount")]
    [InlineData("merchant")]
    public async Task Every_inbox_sort_key_translates(string key)
    {
        var rows = await Service<IInboxService>().List(new InboxFilter(), Ct);

        foreach (var descending in new[] { true, false })
        {
            await rows
                .ApplySort(new SortRequest(key, descending), InboxApi.Sort)
                .SelectDto()
                .ToPageAsync(new PageRequest(), Ct);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task The_inbox_summary_translates()
    {
        await Service<IInboxService>().Summary(Ct);
    }

    /// <summary>
    /// The linked banks, which the account page and the inbox both read. Nothing is linked
    /// in the seed data, so this is purely about the query holding together.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_linked_banks_translate()
    {
        await Service<IBankConnectionService>().Mine(Ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_group_balances_translate()
    {
        var balances = await Service<IGroupService>().GetGroupNetBalance(await AGroupOfTheirs(), Ct);

        await balances.ToListAsync(Ct);
    }

    /// <summary>
    /// The seeder writes expenses that belong to nobody but their payer -- petrol, a
    /// dentist, a birthday present for somebody in the group. They are the only rows that
    /// exercise the personal ledger end to end, and before phase 2 there was no way to
    /// write one: every expense went into a group, the hidden "Personal" one if no other.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_seed_data_includes_personal_expenses()
    {
        var context = Service<AppDbContext>();

        var personal = await context.Set<Expense>()
            .Where(expense => expense.GroupId == null)
            .Include(expense => expense.Splits)
            .ToListAsync(Ct);

        Assert.NotEmpty(personal);

        // No group means no category and nobody to divide with: one share, the payer's own,
        // for the whole amount.
        Assert.All(personal, expense =>
        {
            Assert.Null(expense.CategoryId);

            var share = Assert.Single(expense.Splits);

            Assert.Equal(expense.UserId, share.UserId);
            Assert.Equal(expense.Amount, share.Amount);
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task The_expense_listing_translates()
    {
        var expenses = await Service<ITransactionService>().List(Ct);

        await expenses.ToListAsync(Ct);
    }

    /// <summary>
    /// The personal half of the listing: expenses in no group at all. A separate case
    /// because the filter reaches a nullable navigation, and the search clause has to stay
    /// null-safe over it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_personal_filter_and_the_search_translate()
    {
        var expenses = await Service<ITransactionService>().List(Ct);

        foreach (var personal in new bool?[] { null, true, false })
        {
            await expenses
                .ApplyFilter(new TransactionFilter(Personal: personal, Search: "co"))
                .ToListAsync(Ct);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Every_expense_sort_key_translates()
    {
        var expenses = await Service<ITransactionService>().List(Ct);

        foreach (var key in new[] { "dateTime", "amount", "name", "category", "group", "paidBy" })
        {
            await expenses
                .ApplySort(new SortRequest(key), TransactionApi.Sort)
                .ToListAsync(Ct);
        }
    }
}
