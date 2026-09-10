using GroupSplit.API.Endpoints;
using GroupSplit.API.Errors;
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

    /// <summary>
    /// A group's ledger: the merged listing, its filters, and the two correlated subqueries
    /// behind the running balance.
    /// </summary>
    /// <remarks>
    /// The balance column is the reason this test matters more than it used to. It is a
    /// pair of aggregate subqueries per row, each correlated on a two-part comparison over
    /// the date and the id -- exactly the shape the in-memory provider evaluates on the
    /// client without complaining and Npgsql either translates or refuses outright.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task A_groups_ledger_translates()
    {
        var group = await AGroupOfTheirs();
        var activity = await Service<IGroupService>().GetGroupActivity(group, Ct);
        var me = Service<ICurrentUser>().User.Id;

        // Through the endpoint's own filtering, ordering and projection, which is where the
        // shape that has to translate actually lives.
        foreach (var kind in new ActivityKind?[] { null, ActivityKind.Expense, ActivityKind.Transfer })
        {
            await activity
                .ApplyFilter(new ActivityFilter(
                    From: DateTimeOffset.UtcNow.AddYears(-1),
                    To: DateTimeOffset.UtcNow,
                    Kind: kind,
                    Search: "a"))
                .ApplySort(new SortRequest(), GroupApi.ActivitySort)
                .SelectLedgerDto(me, activity)
                .ToPageAsync(new PageRequest(), Ct);
        }
    }

    /// <summary>
    /// The home page's feed: everything in every group the caller is in, plus their own,
    /// with the group name and their share on each row.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_cross_group_activity_feed_translates()
    {
        var activity = await Service<IGroupService>().GetUserActivity(Ct);
        var me = Service<ICurrentUser>().User.Id;

        await activity
            .OrderByDescending(transaction => transaction.DateTime)
            .ThenByDescending(transaction => transaction.Id)
            .SelectUserActivityDto(me)
            .ToPageAsync(new PageRequest(), Ct);
    }

    /// <summary>
    /// Every member balance in every group the caller is in, which is what the cross-group
    /// settlement plan is computed from.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Every_group_balance_translates_in_one_read()
    {
        await (await Service<IGroupService>().GetAllGroupNetBalances(Ct)).ToListAsync(Ct);
    }

    /// <summary>
    /// The settlement plan and the history behind it, both of which reach through a
    /// transfer's splits to name the other end.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_cross_group_settlement_plan_translates()
    {
        await Service<ISettlementService>().GetPlan(Ct);

        await (await Service<ISettlementService>().GetHistory(Ct))
            .OrderByDescending(settlement => settlement.DateTime)
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
    /// <remarks>
    /// The date key is the one to watch: it orders by the authorized date falling back to
    /// the posting one, so what has to translate is a <c>COALESCE</c> of two date columns in
    /// an <c>ORDER BY</c> -- which the in-memory provider would sort on the client.
    /// </remarks>
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
        await Service<IInboxService>().Summary(withDuplicates: true, Ct);
    }

    /// <summary>
    /// The candidates behind a duplicate suggestion: the caller's own expenses over a window
    /// of days, and the pairs they have already said no to.
    /// </summary>
    /// <remarks>
    /// The row is never saved. What is being asked is whether the two reads the matcher
    /// makes translate, and they are the same reads whether the row is in the database or
    /// held in a hand.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task The_expenses_a_bank_row_could_already_be_translate()
    {
        var row = new BankTransaction
        {
            ProviderTransactionId = "translation-check",
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = 20m,
            Description = "LIDL 1234",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        await Service<IDuplicateMatcher>().ExpensesLike([row], Ct);
    }

    /// <summary>
    /// The other direction, which reads the imported rows still waiting. It compares the
    /// authorised date falling back to the posting one, so the predicate is a coalesce
    /// against a <c>date</c> column rather than a plain column comparison.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_waiting_rows_an_expense_could_already_be_translate()
    {
        var expense = await Service<AppDbContext>().Set<Expense>()
            .Where(candidate => candidate.UserId == Service<ICurrentUser>().User.Id)
            .OrderBy(candidate => candidate.Id)
            .FirstAsync(Ct);

        await Service<IDuplicateMatcher>().RowsLike(expense, Ct);
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

    /// <summary>
    /// Who a group may record money against: its members, and the addresses it has invited
    /// and is waiting on.
    /// </summary>
    /// <remarks>
    /// One query over the users with an OR across two relationships -- a skip navigation to
    /// the groups, and an exists over the invitations -- which is the shape the in-memory
    /// provider is happiest to evaluate on the client and Npgsql has to turn into a real
    /// predicate. Every expense written in a group goes through it, so it failing to
    /// translate would be every split, not one screen.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task A_groups_participants_translate()
    {
        var group = await AGroupOfTheirs();
        var participants = Service<IGroupParticipants>();

        var people = await participants.Of(group).ToListAsync(Ct);

        await participants.IdsOf(group, Ct);
        await participants.Find(group, people[0].Id, Ct);
        await participants.IsPendingInvitee(group, people[0].Id, Ct);
    }

    /// <summary>
    /// The members listing, which is the participants above plus the flag that says which
    /// of them have joined -- a second reach through the same skip navigation, inside a
    /// constructor call.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_members_listing_translates()
    {
        var group = await AGroupOfTheirs();

        var people = await Service<IGroupParticipants>()
            .Describe(await Service<IGroupService>().GetGroupMembers(group, Ct), group)
            .ToListAsync(Ct);

        Assert.NotEmpty(people);
    }

    /// <summary>
    /// The invitee's own list, which is a join between two sets under a constructor call.
    /// </summary>
    /// <remarks>
    /// <c>GET /invitations</c> is the query this whole test class was written for: it once
    /// ordered by a member of a projected record, passed on the in-memory provider because
    /// that one sorts the objects it has already built, and failed on Npgsql, which cannot
    /// translate the constructor at all. It answers a different question now -- the
    /// invitations whose links this account has opened -- and it is the same shape, so it
    /// gets the same guard.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task The_invitations_I_have_opened_translate()
    {
        var group = await AGroupOfTheirs();
        var invitations = Service<IInvitationService>();

        var pending = await invitations.Invite(group,
            new InviteToGroupRequest { Names = ["Translation check"] }, Ct);

        // Opening one is what puts it in the list, and the write is a read's side effect --
        // so this covers both halves.
        await invitations.Describe(pending[0].Token, Ct);

        await invitations.Mine(Ct);
    }

    /// <summary>
    /// Answering an invitation, over the real database: who takes over what it was holding,
    /// and the writes that move it.
    /// </summary>
    /// <remarks>
    /// The seed data has invitations with shares against them -- they are seeded before the
    /// expenses on purpose -- so this is a hand-over that actually moves rows rather than
    /// one that finds nothing to do. Checked afterwards for the invariant the whole thing
    /// rests on: every transaction still divides into exactly its own amount.
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task Withdrawing_an_invitation_translates_and_keeps_the_shares_adding_up()
    {
        var group = await AGroupOfTheirs();
        var invitations = Service<IInvitationService>();

        var pending = await invitations.ForGroup(group, Ct);

        if (pending.Count == 0)
        {
            pending = await invitations.Invite(group,
                new InviteToGroupRequest { Names = ["Translation check"] }, Ct);
        }

        // The claim page's read, over the real database: a constructor call reaching through
        // two navigations and counting a third, which is the shape that does not translate.
        var described = await invitations.Describe(pending[0].Token, Ct);

        Assert.Equal(group, described.GroupId);

        var closed = await invitations.Withdraw(group, pending[0].Id, Ct);

        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);

        var context = Service<AppDbContext>();

        var transactions = await context.Set<Transaction>()
            .Where(transaction => transaction.GroupId == group)
            .Select(transaction => new
            {
                transaction.Amount,
                Shares = transaction.Splits.Sum(split => split.Amount)
            })
            .ToListAsync(Ct);

        Assert.All(transactions, row => Assert.Equal(row.Amount, row.Shares));
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

    /// <summary>
    /// Squaring up, on the real database. The unit suite runs on EF's in-memory provider,
    /// which evaluates on the client whatever it cannot translate rather than refusing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Settling_up_translates()
    {
        var group = await AGroupOfTheirs();

        // Whether there is anything to settle depends on the seed, and either answer is a
        // query that ran: the conflict is raised after the balances have been read.
        try
        {
            await Service<ISettlementService>().SettleUp(group, new SettleUpRequest(), Ct);
        }
        catch (ConflictException)
        {
        }
    }
}
