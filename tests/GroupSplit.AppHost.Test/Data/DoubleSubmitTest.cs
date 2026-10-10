using System.Data.Common;
using Aspire.Hosting.Testing;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// Two requests that each decide from what they read -- a row still waiting, a balance still
/// owed, a member still there, a name still free -- sent at the same moment: a double click,
/// two tabs, a retry after a timeout. Each ends in one outcome, and the one that loses is
/// told so in a documented code rather than a 500.
/// </summary>
/// <remarks>
/// Deterministic rather than hopeful: one call runs on a context with an interceptor, and
/// just before that call's decisive write the interceptor starts the other one and waits
/// until it has either finished or is waiting on a lock this call holds. That is the order
/// the race needs -- the first has read and not yet written -- produced every time. Before
/// <c>WriteLocks</c> the other call finished in that gap and both wrote; now it waits, and
/// reads what the first one did.
/// <para>
/// The unit suite cannot see any of this: its in-memory provider has no transactions, no row
/// locks and no unique indexes. Each test makes its own group, so the seeded data is left as
/// it was found.
/// </para>
/// </remarks>
public class DoubleSubmitTest(AppHostFixture appHost) : IAsyncLifetime
{
    private readonly AppHostFixture _appHost = appHost;

    /// <summary>Requests made by the call under test, with the interceptor.</summary>
    private ServiceProvider? _services;

    /// <summary>
    /// Requests made by the other call, on connections named so that whether it is waiting
    /// on a lock can be read off <c>pg_stat_activity</c>.
    /// </summary>
    private ServiceProvider? _others;

    private string _connectionString = null!;

    private readonly string _otherApplicationName = $"double-submit-{Guid.NewGuid():N}";

    private RunTheOtherCall _interleave = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _appHost.SeedAsync();

        _connectionString = (await _appHost.Application.GetConnectionStringAsync("db", Ct))!;

        _interleave = new RunTheOtherCall(WhenBlockedOrDone);

        _services = Provider(_connectionString, _interleave);
        _others = Provider(
            new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = _otherApplicationName }
                .ConnectionString,
            interceptor: null);
    }

    /// <summary>
    /// The services as the API registers them, on the retrying strategy the deployed
    /// provider uses, so a transaction here has to be opened the way it has to be there.
    /// </summary>
    private static ServiceProvider Provider(string connectionString, IInterceptor? interceptor)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure());

            if (interceptor is not null)
                options.AddInterceptors(interceptor);
        });
        services.AddDomainServices();
        services.AddSingleton<Amazon.S3.IAmazonS3>(Moq.Mock.Of<Amazon.S3.IAmazonS3>());
        services.Configure<ReceiptStorageOptions>(options => options.BucketName = "receipts-test");

        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();

        if (_others is not null)
            await _others.DisposeAsync();
    }

    // ---- Filing a bank row ------------------------------------------------------------------

    [Fact(Timeout = 180_000)]
    public async Task Filing_a_bank_row_twice_at_once_files_it_once()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "File twice");
        var rowId = await BankRow(owner, 25m);

        var file = new FileBankTransactionRequest { GroupId = groupId, FileAnyway = true };

        var (mine, theirs) = await Race(
            BeforeWriting("Transaction"),
            () => As(owner, scope => Service<IInboxService>(scope).File(rowId, file, Ct)),
            () => AsOther(owner, scope => Service<IInboxService>(scope).File(rowId, file, Ct)));

        AssertOneWonAndOneWasTold(mine, theirs, ErrorCodes.BankTransactionAlreadyFiled);

        using var check = _services!.CreateScope();
        var context = Service<AppDbContext>(check);

        var expenses = await context.Set<Transaction>().Where(t => t.GroupId == groupId).ToListAsync(Ct);
        Assert.Equal(rowId, Assert.Single(expenses).BankTransactionId);
    }

    [Fact(Timeout = 180_000)]
    public async Task Filing_a_bank_row_while_it_is_being_linked_does_one_of_the_two()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "File or link");
        var rowId = await BankRow(owner, 25m);

        var typedIn = await As(owner, scope => Service<ITransactionService>(scope).Create(new CreateTransactionRequest
        {
            Name = "Typed in",
            Amount = 25m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId
        }, Ct).AsTask());

        var (filed, linked) = await Race(
            BeforeWriting("Transaction"),
            () => As(owner, scope => Service<IInboxService>(scope).File(rowId,
                new FileBankTransactionRequest { GroupId = groupId, FileAnyway = true }, Ct)),
            () => AsOther(owner, scope => Service<IInboxService>(scope).Link(rowId,
                new LinkBankTransactionRequest { TransactionId = typedIn.Id }, Ct)));

        AssertOneWonAndOneWasTold(filed, linked, ErrorCodes.BankTransactionAlreadyFiled);

        using var check = _services!.CreateScope();
        var context = Service<AppDbContext>(check);

        // Exactly one expense carries the row, and no expense is left over from the one
        // that lost: filing used to write its expense, then fail on the link and keep it.
        var expenses = await context.Set<Transaction>().Where(t => t.GroupId == groupId).ToListAsync(Ct);
        Assert.Single(expenses, expense => expense.BankTransactionId == rowId);
        Assert.Equal(filed.Failed ? 1 : 2, expenses.Count);
    }

    // ---- Settling up ------------------------------------------------------------------------

    [Fact(Timeout = 180_000)]
    public async Task Settling_up_twice_at_once_settles_once()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Settle twice");

        // The member paid 30 for both of them, so the owner owes 15.
        await As(member, scope => Service<ITransactionService>(scope).Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 30m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            Splits = [new SplitInput { UserId = owner, Amount = 15m }, new SplitInput { UserId = member, Amount = 15m }]
        }, Ct).AsTask());

        var (mine, theirs) = await Race(
            BeforeWriting("Transaction"),
            () => As(owner, scope => Service<ISettlementService>(scope).SettleUp(groupId, new SettleUpRequest(), Ct)),
            () => AsOther(owner, scope => Service<ISettlementService>(scope).SettleUp(groupId, new SettleUpRequest(), Ct)));

        AssertOneWonAndOneWasTold(mine, theirs, ErrorCodes.SettlementNothingToSettle);

        // Square, not owed the whole amount back the other way.
        Assert.Equal(0m, await BalanceOf(groupId, owner));
        Assert.Equal(0m, await BalanceOf(groupId, member));
    }

    // ---- Joining by link --------------------------------------------------------------------

    [Fact(Timeout = 180_000)]
    public async Task Following_a_join_link_twice_at_once_joins_once()
    {
        var (owner, joiner) = await TwoPeople();
        var groupId = await NewGroup(owner, "Join twice");
        var token = await LinkTo(groupId, owner);

        var (mine, theirs) = await Race(
            BeforeWriting("GroupUser"),
            () => As(joiner, scope => Service<IJoinLinkService>(scope).Accept(token, Ct)),
            () => AsOther(joiner, scope => Service<IJoinLinkService>(scope).Accept(token, Ct)));

        // Following a link twice is not an error: both are answered, one as the join.
        Assert.False(mine.Failed, mine.Describe());
        Assert.False(theirs.Failed, theirs.Describe());
        Assert.Single(new[] { mine.Value!, theirs.Value! }, joined => !joined.AlreadyAMember);

        Assert.Equal(1, await MembershipRows(groupId, joiner));
    }

    [Fact(Timeout = 180_000)]
    public async Task Coming_back_by_link_twice_at_once_rejoins_once()
    {
        var (owner, joiner) = await TwoPeople();
        var groupId = await GroupOf(owner, joiner, "Rejoin twice");

        await As(joiner, scope => Service<IGroupService>(scope).Leave(groupId, Ct));

        // Issued after they left, so it may bring them back.
        var token = await LinkTo(groupId, owner);

        var (mine, theirs) = await Race(
            BeforeWriting("GroupUser"),
            () => As(joiner, scope => Service<IJoinLinkService>(scope).Accept(token, Ct)),
            () => AsOther(joiner, scope => Service<IJoinLinkService>(scope).Accept(token, Ct)));

        Assert.False(mine.Failed, mine.Describe());
        Assert.False(theirs.Failed, theirs.Describe());
        Assert.Single(new[] { mine.Value!, theirs.Value! }, joined => !joined.AlreadyAMember);

        Assert.Equal(1, await MembersNamed(groupId, joiner));
    }

    // ---- Leaving ----------------------------------------------------------------------------

    /// <summary>
    /// The member's balance is read as square, and then an expense naming them lands before
    /// their departure is written. They used to leave owing it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task An_expense_naming_somebody_as_they_leave_never_leaves_them_owing()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Leave racing");

        var (left, recorded) = await Race(
            BeforeWriting("GroupUser"),
            () => As(member, scope => Service<IGroupService>(scope).Leave(groupId, Ct)),
            () => AsOther(owner, scope => Service<ITransactionService>(scope).Create(DinnerFor(groupId, owner, member), Ct).AsTask()));

        AssertOneWonAndOneWasTold(left, recorded, ErrorCodes.SplitUserNotInGroup);
        Assert.Equal(0m, await BalanceOf(groupId, member));
    }

    /// <summary>The other way round: the expense has checked who is in, and the member leaves.</summary>
    [Fact(Timeout = 180_000)]
    public async Task Leaving_while_an_expense_naming_you_is_recorded_is_refused_as_unsettled()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Recorded racing");

        var (recorded, left) = await Race(
            BeforeWriting("Transaction"),
            () => As(owner, scope => Service<ITransactionService>(scope).Create(DinnerFor(groupId, owner, member), Ct).AsTask()),
            () => AsOther(member, scope => Service<IGroupService>(scope).Leave(groupId, Ct)));

        AssertOneWonAndOneWasTold(recorded, left, ErrorCodes.GroupMemberNotSettled);
        Assert.Equal(2, await MemberCount(groupId));
    }

    /// <summary>Each of the last two used to see the other still there, and the group emptied.</summary>
    [Fact(Timeout = 180_000)]
    public async Task The_last_two_members_leaving_at_once_leave_one_behind()
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Both leave");

        var (mine, theirs) = await Race(
            BeforeWriting("GroupUser"),
            () => As(owner, scope => Service<IGroupService>(scope).Leave(groupId, Ct)),
            () => AsOther(member, scope => Service<IGroupService>(scope).Leave(groupId, Ct)));

        AssertOneWonAndOneWasTold(mine, theirs, ErrorCodes.GroupCannotLeaveLastMember);
        Assert.Equal(1, await MemberCount(groupId));
    }

    // ---- Names ------------------------------------------------------------------------------

    [Theory(Timeout = 180_000)]
    [InlineData("Food", "food")]
    [InlineData("Food", "Food")]
    public async Task Two_categories_whose_names_differ_only_in_case_are_one_category_and_one_refusal(
        string first, string second)
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Categories");

        var (mine, theirs) = await Race(
            BeforeWriting("Category"),
            () => As(owner, scope => Service<ICategoryService>(scope).Create(
                new CreateCategoryRequest { GroupId = groupId, Name = first }, Ct)),
            () => AsOther(member, scope => Service<ICategoryService>(scope).Create(
                new CreateCategoryRequest { GroupId = groupId, Name = second }, Ct)));

        AssertOneWonAndOneWasTold(mine, theirs, ErrorCodes.CategoryNameTaken);

        using var check = _services!.CreateScope();
        Assert.Equal(1, await Service<AppDbContext>(check).Set<Category>()
            .CountAsync(category => category.Group.Id == groupId && category.Name.ToLower() == "food", Ct));
    }

    [Theory(Timeout = 180_000)]
    [InlineData("Shared", "SHARED")]
    [InlineData("Shared", "Shared")]
    public async Task Two_rules_whose_names_differ_only_in_case_are_one_rule_and_one_refusal(
        string first, string second)
    {
        var (owner, member) = await TwoPeople();
        var groupId = await GroupOf(owner, member, "Rules");

        var (mine, theirs) = await Race(
            BeforeWriting("SplitRule"),
            () => As(owner, scope => Service<ISplitRuleService>(scope).Create(
                new CreateSplitRuleRequest { GroupId = groupId, Name = first, Definition = new EvenSplitRuleDto() }, Ct)),
            () => AsOther(member, scope => Service<ISplitRuleService>(scope).Create(
                new CreateSplitRuleRequest { GroupId = groupId, Name = second, Definition = new EvenSplitRuleDto() }, Ct)));

        AssertOneWonAndOneWasTold(mine, theirs, ErrorCodes.SplitRuleNameTaken);

        using var check = _services!.CreateScope();
        Assert.Equal(1, await Service<AppDbContext>(check).Set<SplitRule>()
            .CountAsync(rule => rule.Group.Id == groupId && rule.Name.ToLower() == "shared", Ct));
    }

    // ---- The race -----------------------------------------------------------------------------

    /// <summary>What one call came to: its answer, or what it was refused with.</summary>
    private sealed record Outcome<T>(T? Value, Exception? Error)
    {
        public bool Failed => Error is not null;

        /// <summary>
        /// The refusal the API answers this with, as the exception handlers map it; null
        /// for anything that would be a 500.
        /// </summary>
        public DomainException? Refusal => Error switch
        {
            DomainException refusal => refusal,
            null => null,
            _ => DatabaseConflicts.Translate(Error)
        };

        public string Describe() => Error?.ToString() ?? $"answered {Value}";
    }

    private static async Task<Outcome<T>> Settle<T>(Task<T> call)
    {
        try
        {
            return new Outcome<T>(await call, null);
        }
        catch (Exception exception)
        {
            return new Outcome<T>(default, exception);
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/>, and <paramref name="other"/> just before
    /// <paramref name="call"/> sends the first command <paramref name="at"/> picks out.
    /// </summary>
    private async Task<(Outcome<T> Call, Outcome<TOther> Other)> Race<T, TOther>(
        Func<DbCommand, bool> at, Func<Task<T>> call, Func<Task<TOther>> other)
    {
        Task<Outcome<TOther>>? theirs = null;

        _interleave.Arm(at, () =>
        {
            theirs = Task.Run(() => Settle(other()));
            return theirs;
        });

        var mine = await Settle(call());

        Assert.True(theirs is not null, "The other call never ran, so the order under test was not produced.");

        return (mine, await theirs!);
    }

    private Task<(Outcome<T> Call, Outcome<bool> Other)> Race<T>(
        Func<DbCommand, bool> at, Func<Task<T>> call, Func<Task> other) =>
        Race(at, call, async () =>
        {
            await other();
            return true;
        });

    private Task<(Outcome<bool> Call, Outcome<TOther> Other)> Race<TOther>(
        Func<DbCommand, bool> at, Func<Task> call, Func<Task<TOther>> other) =>
        Race(at, async () =>
        {
            await call();
            return true;
        }, other);

    private Task<(Outcome<bool> Call, Outcome<bool> Other)> Race(
        Func<DbCommand, bool> at, Func<Task> call, Func<Task> other) =>
        Race(at, call, async () =>
        {
            await other();
            return true;
        });

    /// <summary>Just before the first insert into, or update of, <paramref name="table"/>.</summary>
    private static Func<DbCommand, bool> BeforeWriting(string table) => command =>
        command.CommandText.Contains($"INSERT INTO \"{table}\" (", StringComparison.Ordinal)
        || command.CommandText.Contains($"UPDATE \"{table}\" SET", StringComparison.Ordinal);

    /// <summary>
    /// One of the two did what it asked, and the other was refused with
    /// <paramref name="code"/> -- a documented 4xx, never a 500.
    /// </summary>
    private static void AssertOneWonAndOneWasTold<T, TOther>(Outcome<T> mine, Outcome<TOther> theirs, string code)
    {
        Assert.True(mine.Failed != theirs.Failed,
            $"Expected exactly one of the two to succeed.\nThis call: {mine.Describe()}\nThe other: {theirs.Describe()}");

        var (error, refusal) = mine.Failed ? (mine.Error, mine.Refusal) : (theirs.Error, theirs.Refusal);

        Assert.True(refusal is not null, $"The one that lost would have been a 500: {error}");
        Assert.InRange(refusal.Status, 400, 499);
        Assert.Equal(code, refusal.Code);
    }

    /// <summary>
    /// Waits until the other call has either finished or is waiting on a lock -- the one
    /// this call holds, if it holds one. Either way this call may go on: without a lock
    /// the other has already written, which is the race; with one it will read what this
    /// call writes.
    /// </summary>
    private async Task WhenBlockedOrDone(Task other)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);

        while (!other.IsCompleted)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @name AND wait_event_type = 'Lock')
                """, connection);
            command.Parameters.AddWithValue("name", _otherApplicationName);

            if ((bool)(await command.ExecuteScalarAsync(Ct))!)
                return;

            Assert.True(DateTimeOffset.UtcNow < deadline,
                "The other call neither finished nor waited on a lock within 30 seconds.");

            await Task.Delay(20, Ct);
        }
    }

    /// <summary>
    /// Runs the other call once, just before the first command its predicate picks out --
    /// on the call under test's context only, since the other call's context has no
    /// interceptor.
    /// </summary>
    private sealed class RunTheOtherCall(Func<Task, Task> whenBlockedOrDone) : DbCommandInterceptor
    {
        private Func<DbCommand, bool>? _at;
        private Func<Task>? _other;

        public void Arm(Func<DbCommand, bool> at, Func<Task> other)
        {
            _at = at;
            _other = other;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunIfDue(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunIfDue(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await RunIfDue(command);
            return result;
        }

        private async Task RunIfDue(DbCommand command)
        {
            if (_at is not { } at || _other is not { } other || !at(command))
                return;

            _at = null;
            _other = null;

            await whenBlockedOrDone(other());
        }
    }

    // ---- Setting up ---------------------------------------------------------------------------

    private static T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>A request of the call under test's, as one person.</summary>
    private Task<T> As<T>(Guid userId, Func<IServiceScope, Task<T>> work) => Request(_services!, userId, work);

    private Task As(Guid userId, Func<IServiceScope, Task> work) =>
        Request(_services!, userId, async scope =>
        {
            await work(scope);
            return true;
        });

    /// <summary>A request of the other call's, as one person.</summary>
    private Task<T> AsOther<T>(Guid userId, Func<IServiceScope, Task<T>> work) => Request(_others!, userId, work);

    private Task AsOther(Guid userId, Func<IServiceScope, Task> work) =>
        Request(_others!, userId, async scope =>
        {
            await work(scope);
            return true;
        });

    private async Task<T> Request<T>(ServiceProvider services, Guid userId, Func<IServiceScope, Task<T>> work)
    {
        using var scope = services.CreateScope();

        var user = await Service<AppDbContext>(scope).Set<User>()
            .FirstAsync(candidate => candidate.Id == userId, Ct);

        Service<ICurrentUserInitializer>(scope).Initialize(user);

        return await work(scope);
    }

    /// <summary>Two seeded people with accounts, in a stable order.</summary>
    private async Task<(Guid First, Guid Second)> TwoPeople()
    {
        using var scope = _services!.CreateScope();

        var people = await Service<AppDbContext>(scope).Set<User>()
            .Where(user => user.Email != null && user.Kind == UserKind.Account)
            .OrderBy(user => user.Email)
            .Select(user => user.Id)
            .Take(2)
            .ToListAsync(Ct);

        return (people[0], people[1]);
    }

    private Task<Guid> NewGroup(Guid ownerId, string name) =>
        As(ownerId, async scope => (await Service<IGroupService>(scope).CreateGroup(
            new CreateGroupRequest { Name = $"{name} {Guid.NewGuid():N}" }, Ct)).Id);

    private Task<string> LinkTo(Guid groupId, Guid memberId) =>
        As(memberId, async scope => (await Service<IJoinLinkService>(scope).Create(groupId, Ct)).Token);

    /// <summary>A group of the two, the second joined by link.</summary>
    private async Task<Guid> GroupOf(Guid ownerId, Guid memberId, string name)
    {
        var groupId = await NewGroup(ownerId, name);
        var token = await LinkTo(groupId, ownerId);

        await As(memberId, scope => Service<IJoinLinkService>(scope).Accept(token, Ct));

        return groupId;
    }

    /// <summary>An imported row waiting in <paramref name="ownerId"/>'s inbox.</summary>
    private async Task<Guid> BankRow(Guid ownerId, decimal amount)
    {
        using var scope = _services!.CreateScope();
        var context = Service<AppDbContext>(scope);

        var connection = new BankConnection
        {
            UserId = ownerId,
            Provider = "fake",
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Fake Bank",
            AccessTokenCiphertext = "not-a-token",
            LinkedAt = DateTimeOffset.UtcNow
        };

        var account = new LinkedAccount { ProviderAccountId = "acc-1", Name = "Everyday", Type = "depository" };
        connection.Accounts.Add(account);

        var row = new BankTransaction
        {
            Account = account,
            ProviderTransactionId = $"txn-{Guid.NewGuid():N}",
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = amount,
            Description = "Corner shop",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        context.Add(connection);
        context.Add(row);
        await context.SaveChangesAsync(Ct);

        return row.Id;
    }

    /// <summary>30 paid by <paramref name="payer"/>, half of it <paramref name="other"/>'s.</summary>
    private static CreateTransactionRequest DinnerFor(Guid groupId, Guid payer, Guid other) => new()
    {
        Name = "Dinner",
        Amount = 30m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = groupId,
        PaidByUserId = payer,
        Splits = [new SplitInput { UserId = payer, Amount = 15m }, new SplitInput { UserId = other, Amount = 15m }]
    };

    /// <summary>What <paramref name="userId"/> is owed in the group, negative when they owe.</summary>
    private Task<decimal> BalanceOf(Guid groupId, Guid userId) =>
        As(userId, async scope => await (await Service<IGroupService>(scope).GetGroupNetBalanceFor(groupId, userId, Ct))
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstOrDefaultAsync(Ct));

    /// <summary>The membership rows for the pair, open or closed.</summary>
    private async Task<int> MembershipRows(Guid groupId, Guid userId)
    {
        using var scope = _services!.CreateScope();

        return await Service<AppDbContext>(scope).Set<GroupMembership>()
            .IgnoreQueryFilters()
            .CountAsync(row => row.GroupId == groupId && row.UserId == userId, Ct);
    }

    /// <summary>Whether <paramref name="userId"/> is a current member: 1 or 0.</summary>
    private async Task<int> MembersNamed(Guid groupId, Guid userId)
    {
        using var scope = _services!.CreateScope();

        return await Service<AppDbContext>(scope).Set<GroupMembership>()
            .CountAsync(row => row.GroupId == groupId && row.UserId == userId, Ct);
    }

    private async Task<int> MemberCount(Guid groupId)
    {
        using var scope = _services!.CreateScope();

        return await Service<AppDbContext>(scope).Set<GroupMembership>()
            .CountAsync(row => row.GroupId == groupId, Ct);
    }
}
