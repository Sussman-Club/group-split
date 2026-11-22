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
using Moq;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>
/// The same request sent twice at once (a double click, two tabs, an agent retrying),
/// against the real database (issue 321).
/// </summary>
/// <remarks>
/// The unit suite's in-memory provider runs each request to the end before the next one
/// starts and has no unique indexes, so none of this can happen there. Here a second
/// request is started while the first is held just before its insert: the moment a
/// check-then-write race needs. Without a guard the second one passes the same check.
/// </remarks>
public class DoubleSubmitTest(AppHostFixture appHost) : IAsyncLifetime
{
    private const string Provider = "double-submit-test";

    /// <summary>How long a second request may take to get past the first one.</summary>
    private static readonly TimeSpan Overtake = TimeSpan.FromSeconds(2);

    private readonly BeforeInsert _beforeInsert = new();
    private ServiceProvider _services = null!;
    private Guid? _bankUserId;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await appHost.SeedAsync();

        var connectionString = await appHost.Application.GetConnectionStringAsync("db", Ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(options =>
            options.UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure())
                .AddInterceptors(_beforeInsert));
        services.AddDomainServices();
        services.AddDistributedMemoryCache();
        services.AddHybridCache();
        services.AddSingleton(Mock.Of<ILoginDeletion>());
        services.AddKeyedSingleton(Provider, Mock.Of<IBankConnector>());
        services.Configure<BankingOptions>(options => options.Provider = Provider);
        services.AddSingleton(Mock.Of<Amazon.S3.IAmazonS3>());
        services.Configure<ReceiptStorageOptions>(options => options.BucketName = "double-submit-test");

        _services = services.BuildServiceProvider();
    }

    [Fact(Timeout = 120_000)]
    public async Task Filing_a_bank_row_twice_at_once_adds_one_expense_and_refuses_the_other()
    {
        var (userId, rowId) = await NewBankRow();

        using var first = await As(userId);
        using var second = await As(userId);
        Task<Expense>? overtaking = null;

        _beforeInsert.Arm("Transaction", async () =>
        {
            overtaking = Service<IInboxService>(second).File(rowId, new FileBankTransactionRequest(), Ct);
            await Task.WhenAny(overtaking, Task.Delay(Overtake, Ct));
        });

        var outcomes = await Outcomes(Service<IInboxService>(first).File(rowId, new FileBankTransactionRequest(), Ct),
            () => overtaking);

        AssertOneRefused(outcomes, ErrorCodes.BankTransactionAlreadyFiled);

        using var check = _services.CreateScope();
        var db = Service<AppDbContext>(check);
        Assert.Equal(1, await db.Set<Expense>().CountAsync(expense => expense.GroupId == userId, Ct));
        Assert.Equal(BankTransactionStatus.Filed,
            await db.Set<BankTransaction>().Where(row => row.Id == rowId).Select(row => row.Status).SingleAsync(Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task Settling_up_twice_at_once_records_the_repayment_once()
    {
        var (payer, debtor) = await TwoPeople();
        var groupId = await GroupOf(payer, debtor);

        using (var scope = await As(payer))
        {
            await Service<ITransactionService>(scope).Create(new CreateTransactionRequest
            {
                GroupId = groupId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = payer
            }, Ct);
        }

        using var first = await As(debtor);
        using var second = await As(debtor);
        Task<SettleUpResponse>? overtaking = null;

        _beforeInsert.Arm("Transaction", async () =>
        {
            overtaking = Service<ISettlementService>(second).SettleUp(groupId, new SettleUpRequest(), Ct);
            await Task.WhenAny(overtaking, Task.Delay(Overtake, Ct));
        });

        var outcomes = await Outcomes(Service<ISettlementService>(first).SettleUp(groupId, new SettleUpRequest(), Ct),
            () => overtaking);

        AssertOneRefused(outcomes, ErrorCodes.SettlementNothingToSettle);

        using var check = _services.CreateScope();
        var repaid = await Service<AppDbContext>(check).Set<Transfer>()
            .Where(transfer => transfer.GroupId == groupId)
            .SumAsync(transfer => transfer.Amount, Ct);
        Assert.Equal(50m, repaid);
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_categories_with_one_name_at_once_answer_name_taken_rather_than_500()
    {
        var (owner, _) = await TwoPeople();
        var groupId = await GroupOf(owner);
        var request = new CreateCategoryRequest { GroupId = groupId, Name = "Groceries" };

        using var first = await As(owner);
        using var second = await As(owner);

        _beforeInsert.Arm("Category", () => Service<ICategoryService>(second).Create(request, Ct));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Service<ICategoryService>(first).Create(request, Ct));

        Assert.Equal(ErrorCodes.CategoryNameTaken, refused.Code);
        Assert.True(_beforeInsert.Fired);
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_rules_with_one_name_at_once_answer_name_taken_rather_than_500()
    {
        var (owner, _) = await TwoPeople();
        var groupId = await GroupOf(owner);
        var request = new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "All mine",
            Definition = new PercentSplitRuleDto { Percentages = new Dictionary<Guid, decimal> { [owner] = 100 } }
        };

        using var first = await As(owner);
        using var second = await As(owner);

        _beforeInsert.Arm("SplitRule", () => Service<ISplitRuleService>(second).Create(request, Ct));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Service<ISplitRuleService>(first).Create(request, Ct));

        Assert.Equal(ErrorCodes.SplitRuleNameTaken, refused.Code);
        Assert.True(_beforeInsert.Fired);
    }

    [Fact(Timeout = 120_000)]
    public async Task Ignoring_a_bank_row_while_it_is_filed_leaves_it_filed()
    {
        var (userId, rowId) = await NewBankRow();

        using var first = await As(userId);
        using var second = await As(userId);
        Task? overtaking = null;

        _beforeInsert.Arm("Transaction", async () =>
        {
            overtaking = Service<IInboxService>(second).Ignore(rowId, Ct);
            await Task.WhenAny(overtaking, Task.Delay(Overtake, Ct));
        });

        var outcomes = await Outcomes(Service<IInboxService>(first).File(rowId, new FileBankTransactionRequest(), Ct),
            () => overtaking);

        AssertOneRefused(outcomes, ErrorCodes.BankTransactionAlreadyFiled);

        using var check = _services.CreateScope();
        Assert.Equal(BankTransactionStatus.Filed, await Service<AppDbContext>(check).Set<BankTransaction>()
            .Where(row => row.Id == rowId).Select(row => row.Status).SingleAsync(Ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task Both_ends_settling_up_at_once_record_the_repayment_once()
    {
        var (payer, debtor) = await TwoPeople();
        var groupId = await GroupOf(payer, debtor);

        using (var scope = await As(payer))
        {
            await Service<ITransactionService>(scope).Create(new CreateTransactionRequest
            {
                GroupId = groupId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = payer
            }, Ct);
        }

        using var debtorScope = await As(debtor);
        using var payerScope = await As(payer);
        Task<SettleUpResponse>? overtaking = null;

        _beforeInsert.Arm("Transaction", async () =>
        {
            overtaking = Service<ISettlementService>(payerScope).SettleUp(groupId, new SettleUpRequest(), Ct);
            await Task.WhenAny(overtaking, Task.Delay(Overtake, Ct));
        });

        var outcomes = await Outcomes(
            Service<ISettlementService>(debtorScope).SettleUp(groupId, new SettleUpRequest(), Ct),
            () => overtaking);

        AssertOneRefused(outcomes, ErrorCodes.SettlementNothingToSettle);

        using var check = _services.CreateScope();
        var repaid = await Service<AppDbContext>(check).Set<Transfer>()
            .Where(transfer => transfer.GroupId == groupId)
            .SumAsync(transfer => transfer.Amount, Ct);
        Assert.Equal(50m, repaid);
    }

    [Fact(Timeout = 120_000)]
    public async Task Paying_part_of_a_debt_twice_at_once_records_it_once()
    {
        var (payer, debtor) = await TwoPeople();
        var groupId = await GroupOf(payer, debtor);

        using (var scope = await As(payer))
        {
            await Service<ITransactionService>(scope).Create(new CreateTransactionRequest
            {
                GroupId = groupId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = payer
            }, Ct);
        }

        var payment = new SettleRequest
        {
            UserId = payer,
            Amount = 20m,
            Direction = SettlementDirection.YouPaidThem
        };

        using var first = await As(debtor);
        using var second = await As(debtor);
        Task? overtaking = null;

        _beforeInsert.Arm("Transaction", async () =>
        {
            overtaking = Service<ISettlementService>(second).Settle(groupId, payment, Ct);
            await Task.WhenAny(overtaking, Task.Delay(Overtake, Ct));
        });

        var outcomes = await Outcomes(Service<ISettlementService>(first).Settle(groupId, payment, Ct),
            () => overtaking);

        AssertOneRefused(outcomes, ErrorCodes.SettlementAlreadyRecorded);

        using var check = _services.CreateScope();
        var repaid = await Service<AppDbContext>(check).Set<Transfer>()
            .Where(transfer => transfer.GroupId == groupId)
            .SumAsync(transfer => transfer.Amount, Ct);
        Assert.Equal(20m, repaid);
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_categories_differing_only_in_case_at_once_answer_name_taken()
    {
        var (owner, _) = await TwoPeople();
        var groupId = await GroupOf(owner);

        using var first = await As(owner);
        using var second = await As(owner);

        _beforeInsert.Arm("Category", () => Service<ICategoryService>(second)
            .Create(new CreateCategoryRequest { GroupId = groupId, Name = "groceries" }, Ct));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Service<ICategoryService>(first)
            .Create(new CreateCategoryRequest { GroupId = groupId, Name = "Groceries" }, Ct));

        Assert.Equal(ErrorCodes.CategoryNameTaken, refused.Code);
        Assert.True(_beforeInsert.Fired);
    }

    /// <summary>
    /// What both requests came to. The second exists only once the first has reached its
    /// insert, which is why it is read through a function.
    /// </summary>
    private async Task<IReadOnlyList<Exception?>> Outcomes(Task held, Func<Task?> overtaking)
    {
        var heldOutcome = await Settled(held);

        Assert.True(_beforeInsert.Fired);
        var second = overtaking() ?? throw new InvalidOperationException("The second request never started.");

        return [heldOutcome, await Settled(second)];
    }

    private static async Task<Exception?> Settled(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>One request did the work and the other got the ordinary refusal: no 500, nothing twice.</summary>
    private static void AssertOneRefused(IReadOnlyList<Exception?> outcomes, string code)
    {
        Assert.Single(outcomes, outcome => outcome is null);
        var refused = Assert.IsType<ConflictException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal(code, refused.Code);
    }

    /// <summary>Two seeded people with accounts, in a stable order, as <see cref="MembershipDepartureTest"/> uses them.</summary>
    private async Task<(Guid, Guid)> TwoPeople()
    {
        Guid[] seeded =
        [
            Guid.Parse("94F67FC0-F549-46ED-AD81-D6DF79225396"),
            Guid.Parse("B2C1F8E1-8B2A-4E77-9A29-8F0E570A4F64")
        ];

        using var scope = _services.CreateScope();
        var people = await Service<AppDbContext>(scope).Set<User>()
            .Where(user => seeded.Contains(user.Id))
            .OrderBy(user => user.Email)
            .Select(user => user.Id)
            .ToListAsync(Ct);

        return (people[0], people[1]);
    }

    /// <summary>A new group of its own, so a test leaves the seeded groups as it found them.</summary>
    private async Task<Guid> GroupOf(Guid ownerId, Guid? memberId = null)
    {
        using var scope = await As(ownerId);

        var group = await Service<IGroupService>(scope).CreateGroup(
            new CreateGroupRequest { Name = $"Double submit {Guid.NewGuid():N}" }, Ct);

        if (memberId is { } member)
        {
            var link = await Service<IJoinLinkService>(scope).Create(group.Id, Ct);

            using var joining = await As(member);
            await Service<IJoinLinkService>(joining).Accept(link.Token, Ct);
        }

        return group.Id;
    }

    /// <summary>
    /// A throwaway account with one imported bank row in its inbox. Removed again in
    /// <see cref="DisposeAsync"/>, because it is not seeded data.
    /// </summary>
    private async Task<(Guid UserId, Guid RowId)> NewBankRow()
    {
        using var scope = _services.CreateScope();
        var db = Service<AppDbContext>(scope);

        var user = new User { FirstName = "Double submit", Email = $"double-submit-{Guid.NewGuid():N}@test.com" };
        var personal = new Group { Id = user.Id, Name = "Personal" };
        personal.Users.Add(user);

        var connection = new BankConnection
        {
            User = user,
            Provider = Provider,
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Test bank",
            LinkedAt = DateTimeOffset.UtcNow,
            AccessTokenCiphertext = Service<IAccessTokenProtector>(scope).Protect("access-test")
        };
        var account = new LinkedAccount
        {
            Connection = connection,
            ProviderAccountId = $"account-{Guid.NewGuid():N}",
            Name = "Checking",
            Type = "depository"
        };
        var row = new BankTransaction
        {
            Account = account,
            ProviderTransactionId = $"row-{Guid.NewGuid():N}",
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Amount = 23.45m,
            Description = "Corner shop",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        db.AddRange(personal, connection, account, row);
        await db.SaveChangesAsync(Ct);

        _bankUserId = user.Id;
        return (user.Id, row.Id);
    }

    /// <summary>A request of its own, as one person: a scope, a context, a signed-in user.</summary>
    private async Task<IServiceScope> As(Guid userId)
    {
        var scope = _services.CreateScope();

        var user = await Service<AppDbContext>(scope).Set<User>().SingleAsync(user => user.Id == userId, Ct);
        Service<ICurrentUserInitializer>(scope).Initialize(user);

        return scope;
    }

    private static T Service<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        if (_services is null)
        {
            return;
        }

        if (_bankUserId is { } userId)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = Service<AppDbContext>(scope);

            await db.Set<Transaction>().Where(row => row.GroupId == userId).ExecuteDeleteAsync();
            await db.Set<BankTransaction>().Where(row => row.Account.Connection.UserId == userId).ExecuteDeleteAsync();
            await db.Set<LinkedAccount>().Where(row => row.Connection.UserId == userId).ExecuteDeleteAsync();
            await db.Set<BankConnection>().Where(row => row.UserId == userId).ExecuteDeleteAsync();
            await db.Set<GroupMembership>().IgnoreQueryFilters().Where(row => row.UserId == userId).ExecuteDeleteAsync();
            await db.Set<Group>().Where(row => row.Id == userId).ExecuteDeleteAsync();
            await db.Set<User>().Where(row => row.Id == userId).ExecuteDeleteAsync();
        }

        await _services.DisposeAsync();
    }

    /// <summary>
    /// Runs something, once, just before the first insert into a table goes out: the point
    /// at which a request has finished checking and has not yet written.
    /// </summary>
    private sealed class BeforeInsert : DbCommandInterceptor
    {
        private string? _insert;
        private Func<Task>? _action;

        public bool Fired { get; private set; }

        public void Arm(string table, Func<Task> action)
        {
            _insert = $"INSERT INTO \"{table}\"";
            _action = action;
            Fired = false;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RunIfInsert(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunIfInsert(command);
            return result;
        }

        private async Task RunIfInsert(DbCommand command)
        {
            if (_insert is not { } insert || !command.CommandText.Contains(insert, StringComparison.Ordinal)
                || Interlocked.Exchange(ref _action, null) is not { } action)
            {
                return;
            }

            _insert = null;
            Fired = true;

            await action();
        }
    }
}
